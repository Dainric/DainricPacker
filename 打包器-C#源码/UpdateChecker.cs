// Dainric 打包器 · 检查更新
// 作者: Dainric
//
// 更新清单是一个静态 JSON，放在 GitHub Pages 上，不需要自建服务器：
// https://dainric.github.io/DainricPacker/update/latest.json
// 开发/测试时可用环境变量 DAINRIC_UPDATE_URL 指向本地地址。
using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Dainric.Packer
{
    public sealed class UpdateAsset
    {
        public string Url { get; set; }
        public long Size { get; set; }
        public string Sha256 { get; set; }
    }

    public sealed class UpdateInfo
    {
        public string Version { get; set; }
        public string Channel { get; set; }
        public string PublishedAt { get; set; }
        public string Notes { get; set; }
        public UpdateAsset Installer { get; set; }
        public UpdateAsset Portable { get; set; }
    }

    public static class UpdateChecker
    {
        public const string DefaultManifestUrl =
            "https://dainric.github.io/DainricPacker/update/latest.json";

        // 按顺序尝试：测试覆盖地址 → GitHub Pages → jsDelivr
        static string[] ManifestUrls()
        {
            string custom = Environment.GetEnvironmentVariable("DAINRIC_UPDATE_URL");
            if (!string.IsNullOrWhiteSpace(custom))
                return new[] { custom.Trim() };
            return new[]
            {
                DefaultManifestUrl,
                "https://cdn.jsdelivr.net/gh/Dainric/DainricPacker@main/docs/update/latest.json",
                "https://raw.githubusercontent.com/Dainric/DainricPacker/main/docs/update/latest.json"
            };
        }

        public static async Task<UpdateInfo> FetchAsync()
        {
            Exception last = null;
            foreach (string url in ManifestUrls())
            {
                try
                {
                    using (var http = new HttpClient())
                    {
                        http.Timeout = TimeSpan.FromSeconds(10);
                        http.DefaultRequestHeaders.UserAgent.ParseAdd(
                            "DainricPacker/" + MainWindow.AppVersionNumber());
                        string json = await http.GetStringAsync(url).ConfigureAwait(false);
                        var info = JsonSerializer.Deserialize<UpdateInfo>(json,
                            new JsonSerializerOptions
                            {
                                PropertyNameCaseInsensitive = true
                            });
                        if (info == null || string.IsNullOrWhiteSpace(info.Version))
                            throw new InvalidOperationException("更新清单格式不正确");
                        info.Version = info.Version.Trim();
                        return info;
                    }
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }
            throw last ?? new InvalidOperationException("无法获取更新清单");
        }

        /// <summary>比较版本号（支持 4.1.0 / v4.1.0；返回 &gt;0 表示 a 更新）</summary>
        public static int CompareVersions(string a, string b)
        {
            int[] pa = Parse(a), pb = Parse(b);
            int n = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < n; i++)
            {
                int va = i < pa.Length ? pa[i] : 0;
                int vb = i < pb.Length ? pb[i] : 0;
                if (va != vb) return va.CompareTo(vb);
            }
            return 0;
        }

        static int[] Parse(string v)
        {
            v = (v ?? "").Trim().TrimStart('v', 'V');
            int dash = v.IndexOf('-');
            if (dash >= 0) v = v.Substring(0, dash);
            string[] parts = v.Split('.');
            var nums = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                int n;
                if (!int.TryParse(parts[i], out n)) n = 0;
                nums[i] = n;
            }
            return nums;
        }

        public static bool IsNewer(string remote, string current)
            => CompareVersions(remote, current) > 0;

        public static void OpenDownload(UpdateInfo info)
        {
            string url = info != null && info.Installer != null
                ? info.Installer.Url : null;
            if (string.IsNullOrWhiteSpace(url))
                url = "https://github.com/Dainric/DainricPacker/releases/latest";
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { }
        }
    }
}
