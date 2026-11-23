// SPDX-License-Identifier: GPL-3.0-only
// Additional permission under GPLv3 section 7: see NVAPI-EXCEPTION.txt.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Rtx50Temps {
internal sealed class ScanResult {
    public string mode="live-mux",gpu,vbios,adapter,started,finished;
    public bool restored,complete_gpc;
    public int valid_gpc_probes,expected_gpc_probes;
    public List<Reading> gpc_matrix,gpc_with_offsets;
    public Sample frame;

    public static ScanResult Capture(Reader reader){
        var s=reader.Read();
        var matrix=s.sensors.Where(x=>Regex.IsMatch(x.id,@"^GPC\.\d+\.BJT\.\d+$")).ToList();
        int expected=reader.Gpcs.Sum(ch=>ch.Device.Count),valid=matrix.Count(x=>x.c.HasValue);
        return new ScanResult{gpu=s.gpu,vbios=s.vbios,adapter=s.adapter,started=s.time,finished=DateTimeOffset.UtcNow.ToString("o"),
            restored=s.mux_restored,expected_gpc_probes=expected,valid_gpc_probes=valid,complete_gpc=expected>0 && valid==expected && s.mux_restored,
            gpc_matrix=matrix,gpc_with_offsets=s.sensors.Where(x=>x.id.StartsWith("GPC.") && x.id.EndsWith(".OFFSET")).ToList(),frame=s};
    }
}
}
