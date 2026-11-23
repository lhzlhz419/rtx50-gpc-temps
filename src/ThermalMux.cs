// SPDX-License-Identifier: GPL-3.0-only
// Additional permission under GPLv3 section 7: see NVAPI-EXCEPTION.txt.

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Rtx50Temps {
internal sealed class BjtBatch {
    public readonly Dictionary<int,(double Raw,double Offset)> Values=new();
    public bool Restored=true;
    public int Status;
    public double Microseconds;
}
// The only masked update in the live reader is bits 3:0 of this temperature selector.
internal static class ThermalMux {
    const int Selector=0xad0840,Raw=0xad0844,Adjusted=0xad0848;
    const ushort Read=0x15,SelectProvider=0x16;
    static void Item(byte[] packet,int i,ushort op,int reg,uint mask=0,uint value=0){
        int at=8+24*i;
        packet[at]=(byte)op;
        Nvidia.Put(packet,at+4,(uint)reg);
        Nvidia.Put(packet,at+8,mask);
        Nvidia.Put(packet,at+16,value);
    }
    static uint? Result(byte[] packet,int i,ushort op,int reg,uint mask=0){
        int at=8+24*i;
        if(Nvidia.U(packet,at)!=op || Nvidia.U(packet,at+4)!=(uint)reg ||
           BitConverter.ToUInt64(packet,at+8)!=mask || Nvidia.U(packet,at+20)!=0)return null;
        uint word=Nvidia.U(packet,at+16);
        return word==uint.MaxValue || word>>16==0xbadf?null:word;
    }
    public static BjtBatch Poll(Nvidia api,Card card,Channel channel,uint previous){
        var b=new BjtBatch();
        int gpc=channel.Device.Loc-4,first=channel.Start,last=channel.End;
        if(!channel.Device.IsGpc || gpc<0 || gpc>=12 || first>last || first<0 ||
           last>=channel.Device.Count || channel.Device.Count>16 ||
           ((previous>>16)&15)!=gpc || (previous&15)!=last || (previous&0x40000000)!=0)return b;
        int count=last-first+1;
        // Reading the original endpoint last also restores the previous selection.
        var packet=Nvidia.Packet(0x1808,0x11808);
        Nvidia.Put(packet,4,(uint)(1+5*count));
        Item(packet,0,Read,Selector);
        for(int j=first;j<=last;j++){
            int n=1+(j-first)*5;
            Item(packet,n,SelectProvider,Selector,15,(uint)j);
            Item(packet,n+1,Read,Selector);Item(packet,n+2,Read,Raw);
            Item(packet,n+3,Read,Adjusted);Item(packet,n+4,Read,Selector);
        }
        long started=Stopwatch.GetTimestamp();
        try{
            b.Status=api.Call(card,0x2eb3c140,packet);
            if(b.Status!=0 || Nvidia.U(packet,0)!=0x11808 || Nvidia.U(packet,4)!=1+5*count ||
               Result(packet,0,Read,Selector)!=previous)return b;
            for(int j=first;j<=last;j++){
                int n=1+(j-first)*5;uint expected=(previous&~15u)|(uint)j;
                var raw=Reader.Q5(Result(packet,n+2,Read,Raw));
                var offset=Reader.Q5(Result(packet,n+3,Read,Adjusted));
                if(Result(packet,n,SelectProvider,Selector,15).HasValue && Result(packet,n+1,Read,Selector)==expected &&
                   Result(packet,n+4,Read,Selector)==expected && raw.HasValue && offset.HasValue)
                    b.Values.Add(j,(raw.Value,offset.Value));
            }
            return b;
        }finally{
            uint? current=api.ReadRegs(card,Selector)[0];
            b.Restored=current==previous;
            if(!b.Restored && (!current.HasValue || (current.Value&~15u)==(previous&~15u))){
                // Finish through the normal channel getter if the batch was interrupted.
                api.Status(card,1u<<channel.Id);
                b.Restored=api.ReadRegs(card,Selector)[0]==previous;
            }
            if(!b.Restored)b.Values.Clear();
            b.Microseconds=Stopwatch.GetElapsedTime(started).TotalMicroseconds;
        }
    }
}
}
