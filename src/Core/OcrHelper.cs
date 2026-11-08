using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;

namespace AlbionCompanion.Core
{
    /// <summary>Windows 自带 OCR（通过 PowerShell 调用 WinRT）。</summary>
    public static class OcrHelper
    {
        public static List<string> Recognize(Bitmap bmp, out string error)
        {
            error = null;
            var lines = new List<string>();
            if (bmp == null) return lines;
            try
            {
                string tmp = Path.Combine(Path.GetTempPath(), "albion_ocr_" + Guid.NewGuid().ToString("N") + ".png");
                string outFile = Path.Combine(Path.GetTempPath(), "albion_ocr_" + Guid.NewGuid().ToString("N") + ".txt");
                // 放大 2 倍，提升小字识别率
                using (var big = new Bitmap(bmp.Width * 2, bmp.Height * 2))
                using (var g = Graphics.FromImage(big))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(bmp, 0, 0, big.Width, big.Height);
                    big.Save(tmp, System.Drawing.Imaging.ImageFormat.Png);
                }

                string ps1 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "ocr.ps1");
                if (!File.Exists(ps1))
                {
                    ps1 = Path.Combine(Path.GetDirectoryName(new Uri(System.Reflection.Assembly.GetExecutingAssembly().CodeBase).LocalPath), "tools", "ocr.ps1");
                }
                if (!File.Exists(ps1)) { error = "找不到 ocr.ps1: " + ps1; return lines; }

                var psi = new ProcessStartInfo();
                psi.FileName = "powershell.exe";
                psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + ps1 + "\" -ImagePath \"" + tmp + "\" -OutFile \"" + outFile + "\"";
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                var p = Process.Start(psi);
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit(10000);

                if (File.Exists(outFile))
                {
                    foreach (string l in File.ReadAllLines(outFile, new UTF8Encoding(false)))
                    {
                        string s = (l ?? "").Trim();
                        if (s.Length > 0) lines.Add(s);
                    }
                    try { File.Delete(outFile); } catch { }
                }
                if (lines.Count == 0 && err.Length > 0) error = "OCR 错误: " + err.Substring(0, Math.Min(200, err.Length));
                try { File.Delete(tmp); } catch { }
            }
            catch (Exception ex) { error = "异常: " + ex.Message; }
            return lines;
        }
    }
}
