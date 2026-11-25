// SPDX-License-Identifier: GPL-3.0-only
// Additional permission under GPLv3 section 7: see NVAPI-EXCEPTION.txt.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Rtx50Temps {
internal sealed class Window : Form {
    [DllImport("kernel32.dll")] static extern uint GetConsoleProcessList([Out]uint[] ids,uint count);
    [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr win,int command);
    internal static bool OwnConsole(){return GetConsoleProcessList(new uint[2],2)==1;}
    internal static void Open(int index){
        if(OwnConsole())ShowWindow(GetConsoleWindow(),0);
        ApplicationConfiguration.Initialize();
        Application.Run(new Window(index));
    }
    readonly ComboBox cards=new ComboBox();
    readonly ComboBox languages=new ComboBox();
    readonly DataGridView grid=new DataGridView();
    readonly Label status=new Label();
    readonly Button scan=new Button(),back=new Button(),save=new Button();
    readonly CheckBox hide=new CheckBox();
    readonly Timer clock=new Timer{Interval=1000};
    Nvidia api; Reader reader; Sample sample; ScanResult finished;
    Caption statusMessage;
    string savedScan,selectedKey;
    List<Card> devices;

    Window(int index){
        SuspendLayout();
        AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;
        ClientSize=new Size(1100,720);MinimumSize=new Size(720,420);
        var bar=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true,WrapContents=true,Padding=new Padding(6)};
        cards.DropDownStyle=ComboBoxStyle.DropDownList;cards.Width=300;bar.Controls.Add(cards);
        languages.Name="language";languages.AccessibleName="Language / 语言";
        languages.DropDownStyle=ComboBoxStyle.DropDownList;languages.Width=110;
        languages.Items.AddRange(new object[]{"简体中文","English"});
        languages.SelectedIndex=Language.Chinese?0:1;bar.Controls.Add(languages);
        hide.Checked=true;hide.AutoSize=true;hide.Margin=new Padding(9,6,9,3);bar.Controls.Add(hide);
        foreach(var b in new[]{save,scan,back}){
            b.AutoSize=true;b.AutoSizeMode=AutoSizeMode.GrowAndShrink;bar.Controls.Add(b);
        }
        back.Visible=false;
        grid.Dock=DockStyle.Fill;grid.ReadOnly=true;grid.AllowUserToAddRows=false;grid.AllowUserToDeleteRows=false;
        grid.AllowUserToResizeRows=false;grid.RowHeadersVisible=false;grid.SelectionMode=DataGridViewSelectionMode.FullRowSelect;
        grid.BackgroundColor=SystemColors.Window;grid.AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill;
        grid.ColumnHeadersHeightSizeMode=DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        grid.AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.AllCellsExceptHeaders;
        foreach(var (key,weight) in new[]{("id",18),("label",32),("c",10),("src",27),("time",13)}){
            int idx=grid.Columns.Add(key,key);grid.Columns[idx].FillWeight=weight;
            grid.Columns[idx].SortMode=DataGridViewColumnSortMode.NotSortable;
        }
        status.Dock=DockStyle.Bottom;status.Height=32;status.Padding=new Padding(8,6,0,0);status.AutoEllipsis=true;
        grid.Columns["label"].DefaultCellStyle.WrapMode=DataGridViewTriState.True;
        Controls.Add(grid);Controls.Add(status);Controls.Add(bar);
        ApplyLanguage();
        ResumeLayout(true);
        languages.SelectedIndexChanged+=(o,e)=>{
            Language.Set(languages.SelectedIndex==0);Language.Remember();ApplyLanguage();
        };
        Shown+=(o,e)=>Safe(delegate{Connect(index);clock.Start();});
        cards.SelectedIndexChanged+=(o,e)=>{if(api!=null && cards.SelectedIndex>=0)Safe(delegate{
            var c=devices[cards.SelectedIndex];selectedKey=c.Key;reader=new Reader(api,c);finished=null;back.Visible=false;Tick();
        });};
        hide.CheckedChanged+=(o,e)=>Render();
        scan.Click+=(o,e)=>Safe(StartScan);
        save.Click+=(o,e)=>Safe(delegate{using(var dlg=new SaveFileDialog{Filter=JsonFilter(),FileName=Language.Text("温度快照.json","temperatures.json")}){
            if(dlg.ShowDialog(this)==DialogResult.OK)Program.Save(dlg.FileName,finished!=null?(object)finished:sample);
        }});
        back.Click+=(o,e)=>Safe(delegate{finished=null;back.Visible=false;Connect(Math.Max(0,cards.SelectedIndex));});
        clock.Tick+=(o,e)=>Safe(Tick);
        FormClosed+=(o,e)=>{clock.Stop();if(api!=null)api.Dispose();};
    }
    static string JsonFilter()=>Language.Text("JSON 文件|*.json","JSON files|*.json");
    void ApplyLanguage(){
        Text=Language.Text("RTX 50 温度监视器","RTX 50 Temperature Monitor");
        hide.Text=Language.Text("仅显示有效值","Valid readings only");
        save.Text=Language.Text("保存快照","Save snapshot");
        scan.Text=Language.Text("完整探头快照…","Full probe snapshot…");
        back.Text=Language.Text("返回实时","Back to live");
        grid.Columns["id"].HeaderText=Language.Text("通道","Channel");
        grid.Columns["label"].HeaderText=Language.Text("含义","Sensor");
        grid.Columns["c"].HeaderText=Language.Text("温度 °C","Temp. °C");
        grid.Columns["src"].HeaderText=Language.Text("来源 / 状态","Source / status");
        grid.Columns["time"].HeaderText=Language.Text("采样时间","Sample time");
        Render();RenderStatus();
    }
    void Safe(Action action){
        try{action();}catch(Exception e){statusMessage=Caption.FromError(e.GetBaseException());RenderStatus();}
    }
    void Connect(int initial){
        if(api!=null){api.Dispose();api=null;}reader=null;
        var next=new Nvidia();
        try{devices=next.Cards();}catch{next.Dispose();throw;}
        cards.Items.Clear();
        foreach(var c in devices)cards.Items.Add(c.Name+" · PCI "+c.Bus);
        api=next;
        if(devices.Count==0){
            statusMessage=new("未检测到 GeForce RTX 50 系显卡。","No GeForce RTX 50 GPU detected.");RenderStatus();return;
        }
        int i=devices.FindIndex(x=>x.Key==selectedKey);
        cards.SelectedIndex=i>=0?i:Math.Min(initial,devices.Count-1);
    }
    void Tick(){
        if(finished!=null || reader==null)return;
        sample=reader.Read();statusMessage=null;Render();RenderStatus();
    }
    void RenderStatus(){
        if(statusMessage!=null){status.Text=statusMessage.ToString();return;}
        if(finished!=null){
            string restored=finished.restored?Language.Text("选择器核验通过","Selector verified"):Language.Text("选择器状态待核对","Check selector state");
            status.Text=string.Format(Language.Text("同一帧 {0}/{1} 个 GPC 探头 · {2} · {3}", "GPC probes in this frame: {0}/{1} · {2} · {3}"),
                                      finished.valid_gpc_probes,finished.expected_gpc_probes,restored,savedScan);return;
        }
        if(sample==null || reader==null){status.Text="";return;}
        int bjt=sample.sensors.Count(x=>x.id.StartsWith("GPC.") && !x.id.EndsWith(".OFFSET") && x.c.HasValue);
        status.Text=string.Format(Language.Text("{0} · GPC 探头 {1}/{2} · {3} / {4} 项有效", "{0} · GPC probes {1}/{2} · {3} / {4} readings valid"),
            sample.vbios,bjt,reader.Gpcs.Sum(ch=>ch.Device.Count),sample.sensors.Count(x=>x.c.HasValue),sample.sensors.Count);
        if(!sample.mux_restored)status.Text+=Language.Text(" · 选择器回读不一致，本帧轮询已停止"," · Selector mismatch, polling stopped for this frame");
    }
    void Render(){
        List<Reading> data;
        if(finished!=null){
            data=finished.gpc_matrix.ToList();
            data.AddRange(finished.gpc_with_offsets);
            data.AddRange(finished.frame.sensors.Where(r=>!r.id.StartsWith("GPC.")));
        }else if(sample!=null)data=sample.sensors;else return;
        var visible=data.Where(x=>!hide.Checked||x.c.HasValue).ToList();
        int scroll=grid.FirstDisplayedScrollingRowIndex;
        if(grid.Rows.Count!=visible.Count){grid.Rows.Clear();if(visible.Count>0)grid.Rows.Add(visible.Count);}
        for(int i=0;i<visible.Count;i++){
            var r=visible[i]; DateTimeOffset ts;
            grid.Rows[i].SetValues(r.id,r.meaning.ToString(),r.c.HasValue?r.c.Value.ToString("0.00"):"—",
                (r.c.HasValue?r.source:r.status).ToString(),DateTimeOffset.TryParse(r.time,out ts)?ts.LocalDateTime.ToString("HH:mm:ss.fff"):"");
        }
        if(scroll>=0 && scroll<grid.Rows.Count)grid.FirstDisplayedScrollingRowIndex=scroll;
    }
    void StartScan(){
        if(reader==null)return;
        using(var dlg=new SaveFileDialog{Filter=JsonFilter(),FileName=Language.Text("RTX50-完整扫描-","RTX50-full-scan-")+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".json"}){
            if(dlg.ShowDialog(this)!=DialogResult.OK)return;
            if(File.Exists(dlg.FileName)){statusMessage=new("请为扫描结果选一个新文件名。","Choose a new filename for the scan.");RenderStatus();return;}
            savedScan=dlg.FileName;
            var result=ScanResult.Capture(reader);
            Program.Save(savedScan,result);
            finished=result;statusMessage=null;back.Visible=true;Render();RenderStatus();
        }
    }
}
}
