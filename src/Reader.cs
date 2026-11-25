// SPDX-License-Identifier: GPL-3.0-only
// Additional permission under GPLv3 section 7: see NVAPI-EXCEPTION.txt.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;

namespace Rtx50Temps {
internal sealed class Reading {
    public string id,time;
    public Caption meaning,source,status;
    public double? c;
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public GpcIds gpc {get;init;}
    public static readonly Caption Valid = new("有效", "Valid");
    public Reading(string key,Caption label,double? value,Caption origin,Caption issue){
        id=key;meaning=label;c=value;source=origin;status=value.HasValue?Valid:issue;
        time=DateTimeOffset.UtcNow.ToString("o");
    }
}
internal sealed class Sample {
    public string gpu,adapter,vbios,time; public Caption topology; public List<Reading> sensors=new List<Reading>();
    public bool mux_restored=true;
    public int[] logical_to_hardware_gpc;
    public List<MuxCheck> mux_checks=new();
    public void Add(string id,Caption label,double? c,Caption source,Caption issue=null,GpcIds gpc=null){
        sensors.Add(new Reading(id,label,c,source,issue ?? new Caption("接口未给有效值", "No valid value returned")){gpc=gpc});
    }
}
internal sealed record MuxCheck(int gpc,int valid,int nvapi_rc,bool restored,double elapsed_us);
internal sealed class Reader {
    readonly Nvidia api; readonly Card Card; readonly byte[] Info; readonly Topology Top;
    public readonly Caption TopologyError;
    readonly GpcMap gpcMap;
    const int Mux=0xad0840;
    public Reader(Nvidia api,Card c){
        this.api=api;Card=c;
        try{Top=Topology.Parse(api.Image(c),c.Did);}
        catch(InvalidDataException e){TopologyError=Caption.FromError(e);}
        catch(IOException e){TopologyError=Caption.FromError(e);}
        Info=api.Info(Card);
        gpcMap=GpcMap.Read(api,c,Gpcs.Select(ch=>ch.Device.Loc-4));
    }
    internal static double? Q5(uint? word){
        if(!word.HasValue || (word.Value&0x40000000u)==0)return null;
        int q=(int)(word.Value&0x1fff8);
        if((q&0x10000)!=0)q-=0x20000;
        return q/256.0;
    }
    static double? Q8(byte[] b,int off){
        if(b==null)return null;
        int raw=BitConverter.ToInt32(b,off);
        return raw==0xff00 || raw< -80*256 || raw>150*256?(double?)null:raw/256.0;
    }
    static bool Has(byte[] b,int k){return k>=0 && k<32 && b!=null && (Nvidia.U(b,8)&(1u<<k))!=0;}
    public bool RangeMatches(Channel ch,int end){
        if(!Has(Info,ch.Id))return false;
        int off=0x50+ch.Id*0x90;
        return Nvidia.U(Info,off)==3 && Info[off+0x51]==ch.Device.Id && Info[off+0x52]==ch.Start && Info[off+0x53]==end;
    }
    public List<Channel> Gpcs {
        get {return Top==null?new List<Channel>():Top.Channels.Where(x=>x.Kind==2 && x.Device.IsGpc).ToList();}
    }
    static Caption DriverLabel(int k) => k switch {
        0 => new("GPU 带偏移热点", "GPU hotspot (offset applied)"),
        1 => new("GPU 平均温度（驱动口径）", "Mean GPU temperature (driver)"),
        2 => new("显存最高结温", "Maximum VRAM junction temperature"),
        _ => new("驱动温度，区域未识别", "Driver temperature (unknown region)")
    };
    public Sample Read(){
        var s=new Sample{gpu=Card.Name,adapter=Card.Key,vbios=Card.Version,time=DateTimeOffset.UtcNow.ToString("o"),
                         topology=Top!=null?new("活动 VBIOS", "Active VBIOS"):TopologyError,
                         logical_to_hardware_gpc=gpcMap.LogicalToHardware};
        foreach(var p in api.PublicTemperatures(Card)){
            Caption name=p.Item2 switch {
                1 => new("GPU 核心", "GPU core"), 2 => new("显存", "VRAM"),
                4 => new("供电", "Power supply"), 8 => new("板上环境", "Board ambient"),
                _ => new($"目标 {p.Item2}", $"Target {p.Item2}")
            };
            s.Add("PUBLIC."+p.Item1,name,p.Item3>=-80 && p.Item3<=150?(double?)p.Item3:null,
                  new("NVAPI 公共温度传感器", "NVAPI public thermal sensor"));
        }
        uint mask=Info!=null?Nvidia.U(Info,8):0;
        if(mask==0 && Top!=null)foreach(var c in Top.Channels.Where(x=>x.Id<32))mask|=1u<<c.Id;
        if(mask==0)mask=6;
        byte[] all=api.Status(Card,mask);
        for(int k=0;k<32;k++){
            var ch=Top==null?null:Top.Channels.FirstOrDefault(x=>x.Id==k);
            if((mask&(1u<<k))==0 && ch==null)continue;
            // An individual channel request is also how the driver chooses the GPC endpoint.
            byte[] b=ch!=null && ch.Kind==2 || !Has(all,k)?api.Status(Card,1u<<k):all;
            bool found=Has(b,k);int off=0x48+k*0x8c;
            Caption name=ch!=null?ch.Meaning:DriverLabel(k);
            if(ch!=null && ch.Device.Kind==1 && ch.Device.Slot==5 && ch.Device.Op==0)
                name=new($"GPU 平均温度 · 驱动报告（斜率 {ch.Gain}/256，偏移 {ch.Bias}/256 °C）",
                         $"Driver GPU mean (gain {ch.Gain}/256, offset {ch.Bias}/256 °C)");
            s.Add("CH."+k,name,found?Q8(b,off):null,new($"驱动通道 #{k}", $"Driver channel #{k}"));
            if(ch!=null && ch.Kind==2) Providers(s,ch,b);
        }
        if(Top!=null)RawZones(s);
        if(Top!=null && Top.Devices.Any(d=>d.Kind==3 && d.Loc==16)){
            uint?[] sys=api.ReadRegs(Card,0xad0460,0xad0464);
            var origin=new Caption("SYS 独立温度字", "Independent SYS temperature register");
            s.Add("SYS.A",new("SYS 系统侧输入 A · 原温", "SYS input A (raw)"),Q5(sys[0]),origin);
            s.Add("SYS.B",new("SYS 系统侧输入 B · 原温", "SYS input B (raw)"),Q5(sys[1]),origin);
        }
        if(Top!=null)SupplementChannels(s);
        Memory(s);
        return s;
    }
    void Providers(Sample s,Channel ch,byte[] b){
        bool valid=Has(b,ch.Id); int o=0x48+ch.Id*0x8c;
        uint pm=valid && Nvidia.U(b,o+4)==3?Nvidia.U(b,o+0x28):0;
        bool g=ch.Device.IsGpc;
        int count=g?Math.Max(ch.Device.Count,ch.End+1):ch.End+1;
        if(count>16 || ch.Start>ch.End){
            s.Add("PROVIDERS."+ch.Id,new("未识别的探头范围", "Unknown probe range"),null,"ROM",
                  new("配置范围异常", "Invalid configured range"));return;
        }
        int gpc=ch.Device.Loc-4;
        GpcIds ids=g?gpcMap.ForSelector(gpc):null;
        Caption device=ids?.Label() ?? ch.Device.Meaning;
        uint?[] scan=null;
        BjtBatch batch=null;
        if(g && valid && RangeMatches(ch,ch.End)){
            scan=api.ReadRegs(Card,Mux,0xad0844,0xad0848,Mux);
            if(!scan[0].HasValue || scan[0]!=scan[3] || ((scan[0].Value>>16)&15)!=gpc || (scan[0].Value&15)!=ch.End)scan=null;
            if(scan!=null && s.mux_restored){
                batch=ThermalMux.Poll(api,Card,ch,scan[0].Value);
                s.mux_restored=batch.Restored;
                s.mux_checks.Add(new MuxCheck(gpc,batch.Values.Count,batch.Status,batch.Restored,batch.Microseconds));
            }
        }
        for(int j=0;j<count;j++){
            string id=g?"GPC."+gpc+".BJT."+j:"CH."+ch.Id+".P."+j;
            Caption name=g
                ? new($"{device.Zh} · BJT {j} 局部结温（具体单元未公开）", $"{device.En} · BJT {j} junction (physical unit unknown)")
                : new($"{device.Zh} · 探头 {j}", $"{device.En} · probe {j}");
            var offsetLabel=new Caption($"{device.Zh} · BJT {j} 带偏移结温", $"{device.En} · BJT {j} junction (offset applied)");
            double? v=j<10 && (pm&(1u<<j))!=0?Q8(b,o+0x2c+4*j):null;
            Caption source=new("驱动 provider 有效位", "Driver provider validity bit");
            if(g && j==ch.End && scan!=null && Q5(scan[1]).HasValue){
                v=Q5(scan[1]);source=new("GET 后核对 GPC 与探头选择器", "GPC and probe selector verified after GET");
            }
            if(batch!=null && batch.Values.TryGetValue(j,out var pair)){
                s.Add(id,name,pair.Raw,new("硬件轮询 · 选择器双读", "Hardware polling, selector read twice"),gpc:ids);
                s.Add(id+".OFFSET",offsetLabel,pair.Offset,new("同一探头偏移输出", "Offset output of the same probe"),gpc:ids);
                continue;
            }
            s.Add(id,name,v,source,g
                ? new("未取得有效的目标探头读数；检查权限和驱动支持", "No valid probe reading. Check permissions and driver support.")
                : new("驱动未返回该输入的有效温度", "Driver returned no valid temperature for this input"),ids);
            if(g && j==ch.End && scan!=null)
                s.Add(id+".OFFSET",offsetLabel,Q5(scan[2]),new("同一选择器的偏移输出", "Offset output of the same selector"),gpc:ids);
        }
    }
    static void Fill(Sample s,string id,double? value,Caption source){
        if(!value.HasValue)return;
        var row=s.sensors.Find(r=>r.id==id);
        if(row==null || row.c.HasValue)return;
        row.c=value;row.source=source;row.status=Reading.Valid;row.time=DateTimeOffset.UtcNow.ToString("o");
    }
    void SupplementChannels(Sample s){
        foreach(var ch in Top.Channels){
            var d=ch.Device;
            // Hardware source identity and aggregation were checked in RawZones.
            if(ch.Kind==1 && (d.Kind==1 || d.Kind==2) && (ch.Flags&1)==0){
                var z=s.sensors.Find(r=>r.id==(d.Kind==1?"GTZ.":"STZ.")+d.Slot);
                if(z!=null && z.c.HasValue && (!ch.HasClamp || (z.c>=ch.MinC && z.c<=ch.MaxC)))
                    Fill(s,"CH."+ch.Id,z.c,new($"同源 {z.id} · 单位校准", $"Same source as {z.id}, unity gain"));
            }
            if(ch.Kind!=2 || !RangeMatches(ch,ch.End))continue;
            if(d.Kind==4 && d.Loc==0 && ch.Start==0 && ch.End==0){
                // PMU class 23, location 0: bits 16:8 are a signed 9-bit Celsius
                // field in the SCI register. This field has no Q5-valid bit.
                var w=api.ReadRegs(Card,0x118bb0)[0];
                if(w.HasValue && w.Value!=0){
                    int c=(int)((w.Value>>8)&511);
                    if(c>=256)c-=512;
                    if(!ch.HasClamp || (ch.MinC<=c && c<=ch.MaxC))
                        Fill(s,"CH."+ch.Id+".P.0",c,new("SCI mini-TSENSE 0 · PMU 定址，1°C/档", "SCI mini-TSENSE 0, PMU address, 1 °C steps"));
                }
            }
            if(d.Kind==3 && d.Loc==16 && ch.Start==0 && ch.End==1){
                // Each data-port read advances the SYS index. Two reads return it to its initial position.
                var w=api.ReadRegs(Card,0xad0040,0xad0074,0xad0040,0xad0074,0xad0040);
                if(!w[0].HasValue || !w[2].HasValue || w[0]!=w[4] ||
                   (w[0].Value&0x40000000)==0 || (w[2].Value&0x40000000)==0)continue;
                int a=(int)(w[0].Value&15),b=(int)(w[2].Value&15);
                if(a+b!=1 || a==b)continue;
                Fill(s,"CH."+ch.Id+".P."+a,Q5(w[1]),new($"SYS 原温端口 · 硬件 provider {a}", $"SYS raw port, hardware provider {a}"));
                Fill(s,"CH."+ch.Id+".P."+b,Q5(w[3]),new($"SYS 原温端口 · 硬件 provider {b}", $"SYS raw port, hardware provider {b}"));
            }
            if(d.Kind==7 && ch.Start==0 && ch.End<=8){
                var regs=Enumerable.Range(0,1+ch.End/3).Select(i=>0x9a4330+4*i).ToArray();
                var w=api.ReadRegs(Card,regs);
                var values=new double?[ch.End+1];
                for(int i=0;i<=ch.End;i++){
                    if(!w[i/3].HasValue)continue;
                    uint part=w[i/3].Value>>(10*(i%3));
                    if((part&1)==0)continue;
                    int c=(int)((part>>1)&511);if(c>=256)c-=512;
                    values[i]=c;
                }
                var maxChannel=Top.Channels.FirstOrDefault(x=>x.Device.Id==d.Id && x.Kind==3 && x.Id==2);
                var memoryMax=maxChannel==null?null:s.sensors.Find(r=>r.id=="CH."+maxChannel.Id);
                if(memoryMax==null || !memoryMax.c.HasValue || values.Any(v=>!v.HasValue) ||
                   values.Max()!=memoryMax.c)continue;
                for(int i=0;i<values.Length;i++){
                    Fill(s,"CH."+ch.Id+".P."+i,values[i],new("显存热设备快照 · 有效位及 MEM_MAX 交叉核验", "VRAM snapshot, validity and MEM_MAX cross-checked"));
                }
            }
        }
        foreach(var ch in Top.Channels.Where(c=>c.Kind==2)){
            var row=s.sensors.Find(r=>r.id=="CH."+ch.Id);
            if(row!=null && !row.c.HasValue)
                row.status=new("驱动未导出此标量；各探头独立列示", "No scalar from driver. See individual probes.");
        }
    }
    void RawZones(Sample s){
        var zones=Top.Devices.Where(z=>(z.Kind==1 && z.Slot<12)||(z.Kind==2 && z.Slot<6)).ToList();
        if(zones.Count==0)return;
        var regs=new List<int>();
        foreach(var z in zones){
            regs.Add((z.Kind==1?0xad0a10:0xad0b10)+4*z.Slot);
            regs.Add((z.Kind==1?0xad0a50:0xad0b30)+4*z.Slot);
            regs.Add((z.Kind==1?0xad0a90:0xad0b50)+4*z.Slot);
        }
        var w=api.ReadRegs(Card,regs.ToArray());
        for(int i=0;i<zones.Count;i++){
            var z=zones[i];uint? source=w[3*i],conf=w[3*i+1];
            uint wantedG=(z.Mask&0x7ff8u)<<1;
            bool sameSources=source.HasValue && (z.Mask&~0x17ff8u)==0
                && (source.Value&~(wantedG|0x40000000u))==0
                && ((source.Value&0xfff0u)!=0)==(wantedG!=0)
                && ((source.Value&0x40000000u)!=0)==((z.Mask&0x10000u)!=0);
            bool agree=conf.HasValue && (conf.Value&1)!=0 && ((conf.Value>>1)&3)==z.Op && ((conf.Value&8)!=0)==z.Offset;
            string id=(z.Kind==1?"GTZ.":"STZ.")+z.Slot;
            s.Add(id,z.Meaning,agree && sameSources?Q5(w[3*i+2]):null,new("ROM 温区 + 硬件聚合器", "ROM zone and hardware aggregator"),
                  conf.HasValue?new("温区未启用、配置不同或温度无效", "Zone disabled, configuration mismatch or invalid temperature")
                               :new("驱动未提供寄存器读权限", "Driver denied register access"));
        }
    }
    void Memory(Sample s){
        var layout=api.ReadRegs(Card,0x900200)[0];
        var label=new Caption("GDDR 逻辑通道温度", "GDDR logical channel temperature");
        if(!layout.HasValue){s.Add("DQR",label,null,"DQR",new("无法读取显存拓扑", "VRAM topology unavailable"));return;}
        int width=(layout.Value&(1u<<22))!=0?4:2;
        var addrs=new List<int>();
        for(int i=0;i<16/width;i++)addrs.Add(0x9024d0+i*0x4000);
        var flags=api.ReadRegs(Card,addrs.ToArray());
        var ids=new List<int>();var req=new List<int>();
        for(int i=0;i<16;i++){
            int group=i/width,lane=i%width;
            if(!flags[group].HasValue || (flags[group].Value&(1u<<(24+lane)))==0)continue;
            ids.Add(i);req.Add(0x9024d0+group*0x4000);req.Add(0x9024c0+group*0x4000+4*lane);
        }
        if(ids.Count==0){s.Add("DQR",label,null,"DQR",new("无有效显存通道", "No valid VRAM channels"));return;}
        var w=api.ReadRegs(Card,req.ToArray());
        for(int i=0;i<ids.Count;i++){
            int j=ids[i],lane=j%width;double? temp=null;
            if(w[i*2].HasValue && (w[i*2].Value&(1u<<(24+lane)))!=0 && w[i*2+1].HasValue){
                int code=(int)(w[i*2+1].Value>>16)&255;
                if(code<=80)temp=(code-20)*2;
            }
            s.Add("DQR."+j,new($"显存逻辑通道 {j} · GDDR 结温（2°C/档）", $"VRAM channel {j} · GDDR junction (2 °C steps)"),
                  temp,new("DQR 温度码", "DQR temperature code"));
        }
    }
}
}
