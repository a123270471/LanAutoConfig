using System.Text.Json;

namespace LanAutoConfig.Common
{
    /// <summary>一套命名模板方案（控制端“模板方案库”的单元）。</summary>
    public class TemplateScheme
    {
        public string Name { get; set; }
        public TemplateModel Template { get; set; }
        public DateTime SavedAt { get; set; }
    }

    /// <summary>
    /// 模板方案库：把当前模板参数（IP 前缀 / 掩码 / 网关 / DNS / 主机名前缀 / DHCP / 工作组 / 时区 / 对时）
    /// 存成命名方案，落盘到 JSON，供不同楼层 / 不同网段之间一键切换。
    /// 读写都做了容错：文件损坏或不可写时返回空列表 / 静默失败，不影响主流程。
    /// </summary>
    public class TemplateLibrary
    {
        private readonly string _path;
        private readonly object _lock = new object();

        public TemplateLibrary(string path) { _path = path; }

        private static readonly JsonSerializerOptions Opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>列出全部方案（按保存时间倒序，最新的在前）。</summary>
        public List<TemplateScheme> List()
        {
            try
            {
                if (!File.Exists(_path)) return new List<TemplateScheme>();
                var list = JsonSerializer.Deserialize<List<TemplateScheme>>(File.ReadAllText(_path, Encoding.UTF8), Opts);
                if (list == null) return new List<TemplateScheme>();
                list.Sort((a, b) => b.SavedAt.CompareTo(a.SavedAt));
                return list;
            }
            catch
            {
                return new List<TemplateScheme>();
            }
        }

        /// <summary>按名称取方案（不区分大小写）；不存在返回 null。</summary>
        public TemplateScheme Get(string name)
            => List().FirstOrDefault(x => string.Equals(x.Name, name?.Trim() ?? "", StringComparison.OrdinalIgnoreCase));

        /// <summary>保存 / 覆盖一个命名方案（名称去空格；空名直接忽略）。</summary>
        public void Save(string name, TemplateModel t)
        {
            name = (name ?? "").Trim();
            if (string.IsNullOrEmpty(name) || t == null) return;
            lock (_lock)
            {
                var list = List();
                var existing = list.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing.Template = Clone(t);
                    existing.SavedAt = DateTime.Now;
                }
                else
                {
                    list.Add(new TemplateScheme { Name = name, Template = Clone(t), SavedAt = DateTime.Now });
                }
                Write(list);
            }
        }

        /// <summary>删除一个命名方案。</summary>
        public void Delete(string name)
        {
            name = (name ?? "").Trim();
            if (string.IsNullOrEmpty(name)) return;
            lock (_lock)
            {
                var list = List();
                int removed = list.RemoveAll(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (removed > 0) Write(list);
            }
        }

        private void Write(List<TemplateScheme> list)
        {
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(list, Opts), Encoding.UTF8);
                if (File.Exists(_path)) File.Delete(_path);
                File.Move(tmp, _path);
            }
            catch { }
        }

        private static TemplateModel Clone(TemplateModel t) => t == null ? null : new TemplateModel
        {
            IpPrefix = t.IpPrefix,
            SubnetMask = t.SubnetMask,
            Gateway = t.Gateway,
            Dns1 = t.Dns1,
            Dns2 = t.Dns2,
            HostNamePrefix = t.HostNamePrefix,
            IsDhcp = t.IsDhcp,
            Workgroup = t.Workgroup,
            TimeZoneId = t.TimeZoneId,
            SyncTime = t.SyncTime
        };
    }
}
