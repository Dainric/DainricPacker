// Dainric 打包器 · 产物代码签名
// 作者: Dainric
//
// 做 PyInstaller 单文件产物的人迟早会遇到杀软误报，给产物加数字签名
// 是最有效的缓解方式。这里不内置证书：用户提供 .pfx 和密码，
// 打包成功后调用 Windows 自带的 Authenticode（Set-AuthenticodeSignature）
// 给产物签名，不依赖 Windows SDK / signtool。
using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Dainric.Packer
{
    public static class CodeSigner
    {
        public static bool IsConfigured(string pfx)
            => !string.IsNullOrWhiteSpace(pfx);

        public static bool Sign(string file, string pfx, string password,
                                string timestampUrl,
                                Action<string, string> log, out string error)
        {
            error = null;
            if (!IsConfigured(pfx)) { error = "未配置签名证书"; return false; }
            if (!File.Exists(pfx)) { error = "签名证书不存在: " + pfx; return false; }
            if (!File.Exists(file)) { error = "待签名文件不存在: " + file; return false; }

            string ps = Path.Combine(Environment.SystemDirectory,
                @"WindowsPowerShell\v1.0\powershell.exe");
            if (!File.Exists(ps)) { error = "找不到 Windows PowerShell，无法签名"; return false; }

            string tag = Guid.NewGuid().ToString("N");
            string scriptFile = Path.Combine(Path.GetTempPath(),
                "dainric_sign_" + tag + ".ps1");
            string resultFile = Path.Combine(Path.GetTempPath(),
                "dainric_sign_" + tag + ".txt");

            string script =
                "$ErrorActionPreference = 'Stop'\n"
                + "$result = '" + Esc(resultFile) + "'\n"
                + "try {\n"
                + "  $pfx = '" + Esc(pfx) + "'\n"
                + "  $pwd = '" + Esc(password ?? "") + "'\n"
                + "  $file = '" + Esc(file) + "'\n"
                + "  $ts = '" + Esc(timestampUrl ?? "") + "'\n"
                + "  $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($pfx, $pwd)\n"
                + "  if ($ts.Length -gt 0) {\n"
                + "    Set-AuthenticodeSignature -FilePath $file -Certificate $cert -TimestampServer $ts -ErrorAction Stop | Out-Null\n"
                + "  } else {\n"
                + "    Set-AuthenticodeSignature -FilePath $file -Certificate $cert -ErrorAction Stop | Out-Null\n"
                + "  }\n"
                + "  $sig = Get-AuthenticodeSignature -FilePath $file\n"
                + "  if ($sig.SignerCertificate -eq $null) { throw ('签名失败: ' + $sig.StatusMessage) }\n"
                + "  [System.IO.File]::WriteAllText($result, 'OK|' + $sig.Status + '|' + $sig.SignerCertificate.Subject, [System.Text.Encoding]::UTF8)\n"
                + "} catch {\n"
                + "  [System.IO.File]::WriteAllText($result, 'ERR|' + $_.Exception.Message, [System.Text.Encoding]::UTF8)\n"
                + "  exit 1\n"
                + "}\n";

            try
            {
                File.WriteAllText(scriptFile, script, new UTF8Encoding(true));
                try { if (File.Exists(resultFile)) File.Delete(resultFile); } catch { }

                var psi = new ProcessStartInfo(ps)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \""
                                + scriptFile + "\""
                };
                log?.Invoke("[*] 正在签名: " + Path.GetFileName(file), "dim");
                using (var p = Process.Start(psi))
                {
                    if (p == null) { error = "无法启动 PowerShell 签名进程"; return false; }
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(120000))
                    {
                        try { p.Kill(true); } catch { }
                        error = "签名超时";
                        return false;
                    }
                }

                string result = null;
                try { if (File.Exists(resultFile)) result = File.ReadAllText(resultFile, Encoding.UTF8); }
                catch { }
                if (string.IsNullOrEmpty(result)) { error = "签名进程没有返回结果"; return false; }
                if (result.StartsWith("OK|", StringComparison.Ordinal))
                {
                    log?.Invoke("[+] 产物已签名: " + Path.GetFileName(file)
                        + "  " + result.Substring(3), "ok");
                    return true;
                }
                error = result.StartsWith("ERR|", StringComparison.Ordinal)
                    ? result.Substring(4) : result;
                if (error.Length > 600) error = error.Substring(0, 600);
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                try { if (File.Exists(scriptFile)) File.Delete(scriptFile); } catch { }
                try { if (File.Exists(resultFile)) File.Delete(resultFile); } catch { }
            }
        }

        static string Esc(string s) => (s ?? "").Replace("'", "''");
    }
}
