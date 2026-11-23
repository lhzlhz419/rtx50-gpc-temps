// SPDX-License-Identifier: GPL-3.0-only
// Additional permission under GPLv3 section 7: see NVAPI-EXCEPTION.txt.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Rtx50Temps {
internal sealed class Card {
    public IntPtr Handle; public string Name,Version; public uint Pci,Sub,Bus; public int Index;
    public string Key { get { return Bus+"-"+Pci.ToString("X8")+"-"+Sub.ToString("X8"); } }
    public int Did {get{return (int)(Pci>>16);}}
}
internal sealed class Nvidia : IDisposable {
    [DllImport("nvapi64.dll",CallingConvention=CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern IntPtr nvapi_QueryInterface(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Simple();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int EnumGpu([Out,MarshalAs(UnmanagedType.LPArray,SizeConst=64)]IntPtr[] h,out int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int TextGet(IntPtr h,[Out]byte[] text);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Num(IntPtr h,out uint num);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Ident(IntPtr h,out uint pci,out uint sub,out uint rev,out uint ext);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Get(IntPtr h,IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Thermal(IntPtr h,int target,IntPtr buffer);
    readonly Dictionary<uint,Delegate> functions=new Dictionary<uint,Delegate>();
    T Fn<T>(uint id) where T:class {
        Delegate d;
        if(!functions.TryGetValue(id,out d)){
            IntPtr addr=nvapi_QueryInterface(id);
            d=addr==IntPtr.Zero?null:Marshal.GetDelegateForFunctionPointer(addr,typeof(T));
            functions[id]=d;
        }
        return d as T;
    }
    public Nvidia(){
        var init=Fn<Simple>(0x0150e828);
        if(init==null || init()!=0)throw new Caption("NVIDIA 驱动初始化失败", "NVIDIA driver initialization failed").IoError();
    }
    public void Dispose(){var unload=Fn<Simple>(0xd22bdd7e); if(unload!=null)unload();}
    public static uint U(byte[] b,int o){return BitConverter.ToUInt32(b,o);}
    public static void Put(byte[] b,int o,uint v){Buffer.BlockCopy(BitConverter.GetBytes(v),0,b,o,4);}
    static void Put16(byte[] b,int o,ushort v){Buffer.BlockCopy(BitConverter.GetBytes(v),0,b,o,2);}
    string StringOf(uint id,IntPtr h){
        var fn=Fn<TextGet>(id); byte[] s=new byte[64];
        return fn!=null && fn(h,s)==0?Encoding.ASCII.GetString(s).TrimEnd('\0'):"";
    }
    public List<Card> Cards(){
        var handles=new IntPtr[64];int n;
        if(Fn<EnumGpu>(0xe5ac921f)(handles,out n)!=0 || n<0 || n>64)throw new Caption("显卡枚举失败", "GPU enumeration failed").IoError();
        var cards=new List<Card>();
        for(int i=0;i<n;i++){
            var h=handles[i];var name=StringOf(0xceee8e9f,h);
            if(!Regex.IsMatch(name,@"\bGeForce\s+RTX\s+50\d{2}\b",RegexOptions.IgnoreCase))continue;
            uint pci,sub,rev,ext,bus;
            if(Fn<Ident>(0x2ddfb66e)(h,out pci,out sub,out rev,out ext)!=0 || Fn<Num>(0x1be0b8e5)(h,out bus)!=0)continue;
            cards.Add(new Card{Handle=h,Index=cards.Count,Name=name,Version=StringOf(0xa561fd7d,h),Pci=pci,Sub=sub,Bus=bus});
        }
        return cards;
    }
    internal static byte[] Packet(int size,uint ver){byte[] b=new byte[size];Put(b,0,ver);return b;}
    internal int Call(Card c,uint id,byte[] b){
        var fn=Fn<Get>(id); if(fn==null)return -3;
        var pin=GCHandle.Alloc(b,GCHandleType.Pinned);
        try{return fn(c.Handle,pin.AddrOfPinnedObject());}finally{pin.Free();}
    }
    public byte[] Image(Card c){
        var p=Packet(16,0x20010);int rc=Call(c,0xfc13ee11,p);uint size=U(p,4);
        if((rc!=-174 && rc!=0) || size<512 || size>16*1024*1024)throw new Caption($"驱动未提供活动 ROM (NVAPI {rc})", $"Active ROM unavailable (NVAPI {rc})").IoError();
        var data=new byte[size];var pin=GCHandle.Alloc(data,GCHandleType.Pinned);
        try{
            Buffer.BlockCopy(BitConverter.GetBytes(pin.AddrOfPinnedObject().ToInt64()),0,p,8,8);
            rc=Call(c,0xfc13ee11,p);
            uint returned=U(p,4);
            if(rc!=0 || returned>size || returned<512)throw new Caption($"活动 ROM 读回失败 ({rc})", $"Active ROM readback failed ({rc})").IoError();
            if(returned!=size)Array.Resize(ref data,(int)returned);
        }finally{pin.Free();}
        return data;
    }
    public byte[] Info(Card c){
        byte[] p=Packet(0x3650,0x33650);
        return Call(c,0x0bc8163d,p)==0 && U(p,0)==0x33650?p:null;
    }
    public byte[] Status(Card c,uint mask){
        var p=Packet(0x34c8,0x334c8);Put(p,8,mask);
        for(int i=0;i<32;i++)if((mask&(1u<<i))!=0)Put(p,0x48+i*0x8c,0xff00);
        int rc=Call(c,0x65fe3aad,p);
        if(rc==0 && U(p,0)==0x334c8 && U(p,4)<=96)return p;
        if(rc==-3)return null;
        // Use the older temperature-only layout if this driver does not supply v3.
        // Individual reads may still be useful when a mixed channel mask is rejected.
        var shortForm=Packet(0xa8,0x200a8);Put(shortForm,4,mask);
        for(int i=0;i<32;i++)Put(shortForm,0x28+4*i,0xff00);
        if(Call(c,0x65fe3aad,shortForm)!=0 || U(shortForm,0)!=0x200a8)return null;
        p=Packet(0x34c8,0x334c8);uint returned=U(shortForm,4)&mask;Put(p,8,returned);
        for(int i=0;i<32;i++)if((returned&(1u<<i))!=0)Put(p,0x48+0x8c*i,U(shortForm,0x28+4*i));
        return p;
    }
    public List<Tuple<int,int,int>> PublicTemperatures(Card c){
        var list=new List<Tuple<int,int,int>>();var fn=Fn<Thermal>(0xe3640a56);
        if(fn==null)return list;
        var b=Packet(68,0x20044);var pin=GCHandle.Alloc(b,GCHandleType.Pinned);
        try{
            if(fn(c.Handle,15,pin.AddrOfPinnedObject())!=0 || U(b,4)>3)return list;
            for(int i=0;i<U(b,4);i++){
                int p=8+20*i;
                list.Add(Tuple.Create(i,(int)U(b,p+16),BitConverter.ToInt32(b,p+12)));
            }
        }finally{pin.Free();}
        return list;
    }
    public uint?[] ReadRegs(Card c, params int[] addresses){
        if(addresses.Length>256)throw new ArgumentException(Language.Text("NVAPI 批次过大", "NVAPI batch too large"));
        var data=Packet(0x1808,0x11808);Put(data,4,(uint)addresses.Length);
        for(int i=0;i<addresses.Length;i++){int o=8+24*i;Put16(data,o,0x15);Put(data,o+4,(uint)addresses[i]);}
        var values=new uint?[addresses.Length];
        if(Call(c,0x2eb3c140,data)!=0 || U(data,0)!=0x11808 || U(data,4)!=addresses.Length)return values;
        for(int i=0;i<addresses.Length;i++){
            int o=8+24*i;ulong v=BitConverter.ToUInt64(data,o+16);
            if(U(data,o)==0x15 && U(data,o+4)==(uint)addresses[i] && BitConverter.ToUInt64(data,o+8)==0 &&
               v<uint.MaxValue && (v>>16)!=0xbadf)values[i]=(uint)v;
        }
        return values;
    }
}
}
