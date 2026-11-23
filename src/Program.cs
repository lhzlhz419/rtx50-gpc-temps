// SPDX-License-Identifier: GPL-3.0-only
// Additional permission under GPLv3 section 7: see NVAPI-EXCEPTION.txt.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Text.Encodings.Web;
using System.Text.Json;
using static Rtx50Temps.Language;

namespace Rtx50Temps {
internal static class Program {
    internal static class Json {
        static readonly JsonSerializerOptions Options=new JsonSerializerOptions{
            IncludeFields=true,Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        public static string Serialize<T>(T value){return JsonSerializer.Serialize(value,Options);}
        static readonly JsonSerializerOptions PipeOptions=new JsonSerializerOptions{IncludeFields=true};
        public static string ForConsole<T>(T value){return Console.IsOutputRedirected?JsonSerializer.Serialize(value,PipeOptions):Serialize(value);}
        public static T Deserialize<T>(string text){return JsonSerializer.Deserialize<T>(text,Options);}
    }
    static volatile bool quit;
    internal static void Save(string file,object obj){
        string full=Path.GetFullPath(file),tmp=full+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{
            File.WriteAllText(tmp,Json.Serialize(obj),new UTF8Encoding(false));
            if(File.Exists(full))File.Replace(tmp,full,null);else File.Move(tmp,full);
        }finally{if(File.Exists(tmp))File.Delete(tmp);}
    }
    static void Show(Sample s){
        Console.WriteLine("\n{0}   VBIOS {1}   {2:HH:mm:ss}",s.gpu,s.vbios,DateTime.Now);
        Console.WriteLine(Text("拓扑：{0}    适配器：{1}", "Topology: {0}    Adapter: {1}"), s.topology, s.adapter);
        foreach(var r in s.sensors){
            string v=r.c.HasValue?r.c.Value.ToString("0.00",CultureInfo.InvariantCulture)+" °C":"—";
            Console.WriteLine("{0,-20} {1,10}  {2}  [{3}]",r.id,v,r.meaning,r.c.HasValue?r.source:r.status);
        }
        Console.WriteLine(Text("有效 {0} / 枚举 {1} 项。", "{0} of {1} readings valid."),s.sensors.Count(x=>x.c.HasValue),s.sensors.Count);
        if(!s.mux_restored)Console.WriteLine(Text("选择器回读不同，本帧已停止继续轮询。", "Selector readback mismatch. Polling stopped for this frame."));
    }
    static void Help(){
        Console.WriteLine("Rtx50Temps · Windows x64");
        Console.WriteLine(Text("  Rtx50Temps.exe              打开实时温度表，也可使用 --gui [--gpu 0]",
                               "  Rtx50Temps.exe              Open the live window, or use --gui [--gpu 0]"));
        Console.WriteLine(Text("  Rtx50Temps.exe --list       列出显卡", "  Rtx50Temps.exe --list       List GPUs"));
        Console.WriteLine("  Rtx50Temps.exe --once [--gpu 0] [--json] [--save snapshot.json]");
        Console.WriteLine("  Rtx50Temps.exe --watch 1 [--gpu 0] [--samples 30] [--json]");
        Console.WriteLine("  Rtx50Temps.exe --gpu 0 --full-scan snapshot.json");
        Console.WriteLine(Text("  --lang zh|en                本次运行使用中文或英文",
                               "  --lang zh|en                Use Chinese or English for this run"));
        Console.WriteLine(Text("实时与完整扫描都在每个 GPC 采样后恢复探头编号。", "Live and full scans restore the probe selector after each GPC."));
        Console.WriteLine(Text("请在管理员终端运行。--watch 的单位为秒，Ctrl+C 结束。", "Run in an elevated terminal. --watch takes seconds. Press Ctrl+C to stop."));
    }
    // Process the language before reporting errors, keeping option operands such as filenames intact.
    static string[] SelectLanguage(string[] input){
        var remaining=new List<string>();
        for(int i=0;i<input.Length;i++){
            string option=input[i];
            if(option=="--lang"){Language.FromArgument(++i<input.Length?input[i]:null);continue;}
            remaining.Add(option);
            if(option is "--gpu" or "--adapter" or "--watch" or "--samples" or "--save" or "--full-scan" or "--inspect-rom"){
                if(i+1>=input.Length)throw new ArgumentException(Text($"缺少 {option} 的参数。", $"Missing value for {option}."));
                remaining.Add(input[++i]);
            }
        }
        return remaining.ToArray();
    }
    [STAThread] static int Main(string[] args){
        Console.CancelKeyPress+=(o,e)=>{e.Cancel=true;quit=true;};
        try{
            Language.Load();
            args=SelectLanguage(args);
            if(args.Length==0){Window.Open(0);return 0;}
            if(args.Length==1 && (args[0]=="--help" || args[0]=="-h")){Help();return 0;}
            int index=0,limit=0;double interval=0;bool json=false,list=false,gui=false;string full=null,rom=null,save=null,adapter=null;
            for(int i=0;i<args.Length;i++){
                switch(args[i]){
                    case "--gpu":
                        if(!int.TryParse(args[++i],NumberStyles.Integer,CultureInfo.InvariantCulture,out index) || index<0)
                            throw new ArgumentException(Text("--gpu 需要非负整数。", "--gpu requires a non-negative integer."));
                        break;
                    case "--adapter":adapter=args[++i];break;
                    case "--watch":
                        if(!double.TryParse(args[++i],NumberStyles.Float,CultureInfo.InvariantCulture,out interval) || !(interval>=0.2 && interval<=86400))
                            throw new ArgumentException(Text("采样间隔须为 0.2–86400 秒", "Sampling interval must be 0.2–86400 seconds."));break;
                    case "--samples":
                        if(!int.TryParse(args[++i],NumberStyles.Integer,CultureInfo.InvariantCulture,out limit) || limit<1)
                            throw new ArgumentException(Text("采样数须为正整数", "Sample count must be a positive integer."));break;
                    case "--json":json=true;break;
                    case "--gui":gui=true;break;
                    case "--save":save=args[++i];break;
                    case "--list":list=true;break;
                    case "--once":break;
                    case "--full-scan":full=args[++i];break;
                    case "--inspect-rom":rom=args[++i];break;
                    default:throw new ArgumentException(Text($"未知参数 {args[i]}。用 --help 查看用法。", $"Unknown option {args[i]}. Use --help for usage."));
                }
            }
            if(limit>1 && interval==0)throw new ArgumentException(Text("--samples 多帧采集需要 --watch 间隔", "Multiple samples require a --watch interval."));
            if(gui){Window.Open(index);return 0;}
            if(rom!=null){
                var t=Topology.Parse(File.ReadAllBytes(rom),0);
                Console.WriteLine(Json.ForConsole(new {t.PciId,t.Devices,t.Channels,t.ScanRestriction}));return 0;
            }
            if(full!=null){
                if(list || interval!=0)throw new ArgumentException(Text("完整扫描单独运行", "Run a full scan separately."));
                var result=DirectScan(index,full,adapter);
                Console.WriteLine(Text("结果：{0} · GPC 探头 {1}/{2} · 采集方式：{3}", "Saved {0} · GPC probes {1}/{2} · mode: {3}"),
                                  Path.GetFullPath(full),result.valid_gpc_probes,result.expected_gpc_probes,result.mode);
                return result.complete_gpc?0:3;
            }
            using(var api=new Nvidia()){
                var cards=api.Cards();
                if(list){foreach(var c in cards)Console.WriteLine(Text("{0}  {1}  总线 {2}  VBIOS {3}", "{0}  {1}  bus {2}  VBIOS {3}"),c.Index,c.Name,c.Bus,c.Version);return cards.Count>0?0:2;}
                if(index<0 || index>=cards.Count)throw new IOException(Text("未找到指定的 GeForce RTX 50 系显卡。用 --list 查看设备。", "GeForce RTX 50 GPU not found. Use --list to see available devices."));
                if(save!=null && interval!=0)throw new ArgumentException(Text("--save 保存单帧。持续日志使用 --watch 1 --json 重定向。", "--save writes one frame. Redirect --watch 1 --json for a continuous log."));
                var r=new Reader(api,cards[index]);
                int emitted=0;
                do{
                    var sample=r.Read();
                    if(json)Console.WriteLine(Json.ForConsole(sample));else Show(sample);
                    if(save!=null)Save(save,sample);
                    if(interval==0 || ++emitted==limit)break;
                    var until=DateTime.UtcNow.AddSeconds(interval);
                    while(!quit && DateTime.UtcNow<until)Thread.Sleep(100);
                }while(!quit);
            }
            return 0;
        }catch(Exception ex){Console.Error.WriteLine("Rtx50Temps: "+ex.GetBaseException().Message);return 1;}
    }
    static ScanResult DirectScan(int index,string file,string adapter){
        using var api=new Nvidia();
        var cards=api.Cards();
        if(index<0 || index>=cards.Count)throw new IOException(Text("未找到所选 GPU", "Selected GPU not found."));
        var card=cards[index];
        if(adapter!=null && card.Key!=adapter)throw new IOException(Text("设备顺序已变，请重新选择", "Device order changed. Select the GPU again."));
        var reader=new Reader(api,card);
        var result=ScanResult.Capture(reader);
        using(var fs=new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.None)){
            JsonSerializer.Serialize(fs,result,new JsonSerializerOptions{IncludeFields=true,Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping});
        }
        return result;
    }
}
}
