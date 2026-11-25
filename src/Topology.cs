// SPDX-License-Identifier: GPL-3.0-only
// Additional permission under GPLv3 section 7: see NVAPI-EXCEPTION.txt.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Rtx50Temps {
internal sealed class DeviceRow {
    public int Id, Kind, Loc, Count, Slot, Op; public uint Mask; public bool Offset;
    public bool IsGpc { get { return Kind==3 && Loc>=4 && Loc<=15; } }
    public Caption Region {
        get {
            bool g=(Mask&0x7ff8)!=0, s=(Mask&0x10000)!=0;
            if ((Mask&~0x17ff8u)!=0 || (!g&&!s)) return new($"源掩码 0x{Mask:X}", $"Source mask 0x{Mask:X}");
            return g && s ? new Caption("GPC 与 SYS", "GPC and SYS") : g ? "GPC" : "SYS";
        }
    }
    public Caption Meaning {
        get {
            if (Kind==1 || Kind==2) {
                var op = Op switch {
                    0 => new Caption("平均温", "mean temperature"),
                    2 => new Caption("最高温", "maximum temperature"),
                    3 => new Caption("最低温", "minimum temperature"),
                    _ => new Caption("聚合值（算法未识别）", "aggregate (unknown operation)")
                };
                var region = Region;
                return new(region.Zh+(Offset?" 带探头偏移":" 原始")+op.Zh,
                           region.En+" "+op.En+(Offset?" (probe offsets applied)":" (raw)"));
            }
            if (IsGpc) return "GPC "+(Loc-4);
            if (Kind==3 && Loc==16) return new("SYS 系统侧", "SYS");
            if (Kind==4 && Loc==0) return "SCI mini-TSENSE";
            if (Kind==7) return new("显存", "VRAM");
            return new($"热设备 {Id}（类型 {Kind}）", $"Thermal device {Id} (type {Kind})");
        }
    }
}
internal sealed class Channel {
    public int Id, Kind, Start, End, OffsetInImage, Flags, Gain, Bias; public DeviceRow Device;
    public double MinC,MaxC;
    public bool HasClamp {get{return (Flags&2)!=0;}}
    public Caption Meaning {
        get {
            if (Device.Kind==7 && Kind==3) return Id==2
                ? new("显存最高结温（MEM_MAX）", "Maximum VRAM junction temperature (MEM_MAX)")
                : new("显存聚合温度", "Aggregate VRAM temperature");
            var label=Device.Meaning;
            return Kind==2 ? new(label.Zh+" · 分探头通道", label.En+" · per-probe channel") : label;
        }
    }
}
internal sealed class Topology {
    public int PciId; public readonly List<DeviceRow> Devices=new List<DeviceRow>();
    public readonly List<Channel> Channels=new List<Channel>();
    public Caption ScanRestriction;
    internal static int U16(byte[] b,int o){return BitConverter.ToUInt16(b,o);}
    internal static uint U32(byte[] b,int o){return BitConverter.ToUInt32(b,o);}
    static void Check(bool ok,Caption message){if(!ok) throw message.InvalidData();}
    static bool At(byte[] b,int i,byte[] pat){return i>=0 && i<=b.Length-pat.Length && pat.Select((v,k)=>b[i+k]==v).All(x=>x);}
    static readonly byte[] Pcir=Encoding.ASCII.GetBytes("PCIR"), Bit={255,184,66,73,84,0};
    static bool Pci(byte[] b,int off,out int header) {
        header=0;
        if(off<0 || off>b.Length-26 || b[off]!=0x55 || b[off+1]!=0xaa)return false;
        header=off+U16(b,off+24);
        return header<=b.Length-22 && At(b,header,Pcir);
    }
    static Caption InspectConsumers(byte[] b,int token,int tokenSize,int p,int legacy,int efi,IEnumerable<Channel> channels){
        if(tokenSize<0x54)return new Caption("BIT P 缺少温控策略入口", "BIT P has no thermal policy entry");
        var endpoints=new HashSet<int>(channels.Where(ch=>ch.Kind==2 && ch.Device.IsGpc).Select(ch=>ch.Id));
        foreach(int field in new[]{0x50,0x20}){
            uint rel=U32(b,token+field);
            if(rel==0)return new Caption("无法核对温控策略或 VFE 变量", "Cannot verify the thermal policy or VFE variables");
            long a64=p+(long)rel+(rel>=legacy?efi:0);
            if(a64<0 || a64>b.Length-5)return new Caption("策略或 VFE 数据不完整", "Incomplete policy or VFE data");
            int a=(int)a64,h=b[a+1],s=b[a+2],n=b[a+3];
            bool known=field==0x50 ? b[a]==0x20 && h>=4 && s>=8 : b[a]==0x30 && h==14 && s==29 && n==255;
            if(!known || (long)a+h+s*n>b.Length)return new Caption("未识别的温控策略或 VFE 布局", "Unrecognized thermal policy or VFE layout");
            for(int i=0;i<n;i++){
                int r=a+h+i*s;
                if(field==0x50 && b[r]!=0 && endpoints.Contains(b[r+1]))return new Caption("温控策略直接引用 GPC 诊断通道", "A thermal policy directly references a GPC diagnostic channel");
                if(field==0x20 && b[r]==3 && endpoints.Contains(b[r+9]))return new Caption("温度型 VFE 变量直接引用 GPC 诊断通道", "A thermal VFE variable directly references a GPC diagnostic channel");
            }
        }
        return null;
    }
    public static Topology Parse(byte[] b,int expectedPci) {
        for(int p=0;p<=b.Length-26;p++){
            int pc; if(!Pci(b,p,out pc) || b[pc+20]!=0 || U16(b,pc+4)!=0x10de)continue;
            int id=U16(b,pc+6);
            if(expectedPci!=0 && expectedPci!=id)continue;
            int len=U16(b,pc+16)*512, ep;
            if(len<512 || (long)p+len>b.Length)continue;
            int efi=Pci(b,p+len,out ep) && b[ep+20]==3?U16(b,ep+16)*512:0;
            for(int bit=p;bit<=p+len-12;bit++){
                if(!At(b,bit,Bit))continue;
                int hdr=b[bit+8],step=b[bit+9],count=b[bit+10];
                if(hdr<11 || step<6 || (long)bit+hdr+step*count>p+len)continue;
                for(int j=0;j<count;j++){
                    int q=bit+hdr+j*step;
                    if(b[q]!='P' || U16(b,q+2)<0x4c)continue;
                    int token=p+U16(b,q+4);
                    Check(token<=p+len-0x4c,new Caption("BIT P 指针越界", "BIT P pointer is out of bounds"));
                    var t=new Topology{PciId=id};
                    foreach(int field in new[]{0x14,0x48}){
                        uint rel=U32(b,token+field); Check(rel!=0,new Caption("缺少热表", "Missing thermal table"));
                        long addr=p+(long)rel+(rel>=len?efi:0);
                        Check(addr<=b.Length-4,new Caption("热表截断", "Truncated thermal table"));
                        int a=(int)addr;
                        int h=b[a+1],s=b[a+2],n=b[a+3];
                        Check(b[a]==0x20 && h>=4 && s==(field==0x14?12:14) && (long)a+h+s*n<=b.Length,new Caption("不支持或截断的热表", "Unsupported or truncated thermal table"));
                        for(int i=0;i<n;i++){
                            int r=a+h+i*s; int kind=b[r]; if(kind==0)continue;
                            if(field==0x14){
                                uint arg=U32(b,r+4);
                                t.Devices.Add(new DeviceRow{Id=i,Kind=kind,Loc=b[r+4],Count=b[r+1],
                                    Slot=(int)(arg&31),Op=(int)((arg>>5)&7),Offset=(arg&256)!=0,Mask=U32(b,r+8)});
                            }else{
                                var d=t.Devices.FirstOrDefault(x=>x.Id==b[r+2]);
                                if(d==null)continue;
                                t.Channels.Add(new Channel{Id=i,Kind=kind,Device=d,Start=b[r+3],End=b[r+4],
                                    Flags=b[r+1],OffsetInImage=r,Gain=(b[r+1]&1)!=0?BitConverter.ToInt16(b,r+6):256,
                                    Bias=(b[r+1]&1)!=0?BitConverter.ToInt16(b,r+8):0,
                                    MinC=BitConverter.ToInt16(b,r+10)/32.0,MaxC=BitConverter.ToInt16(b,r+12)/32.0});
                            }
                        }
                    }
                    Check(t.Channels.Count>0,new Caption("没有可解析的温度通道", "No temperature channels could be parsed"));
                    if(token+U16(b,q+2)>p+len)throw new Caption("BIT P 长度越界", "BIT P length is out of bounds").InvalidData();
                    t.ScanRestriction=InspectConsumers(b,token,U16(b,q+2),p,len,efi,t.Channels);
                    return t;
                }
            }
        }
        throw new Caption("找不到与显卡匹配的 NVIDIA PCIR/BIT 热表", "No NVIDIA PCIR/BIT thermal table matches the GPU").InvalidData();
    }
}
}
