using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace UsageTracker
{
    /// <summary>
    /// 사용자가 정한 표시 정보. 화면 모드만 쓴다 (기록 모드는 appinfo.tsv만 씀).
    ///   apps.tsv   : 앱ID  사용자 이름  묶음ID  숨김(0/1)  즐겨찾기(0/1)
    ///   groups.tsv : 묶음ID  이름  숨김(0/1)  즐겨찾기(0/1)
    /// (즐겨찾기 열은 v0.8.0에서 추가. 없으면 0으로 읽는다.)
    /// 묶음 키는 "group:" + 묶음ID. 원본 기록은 exe 단위로 남으므로 묶음을 풀면 그대로 다시 나뉜다.
    /// </summary>
    sealed class AppCatalog
    {
        public const string GroupPrefix = "group:";

        sealed class AppEntry { public string CustomName; public string GroupId; public bool Hidden, Favorite; }
        sealed class GroupEntry { public string Name; public bool Hidden, Favorite; }

        readonly string dir;
        readonly Dictionary<string, AppEntry> apps = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, GroupEntry> groups = new Dictionary<string, GroupEntry>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, AppInfo> Info { get; private set; }

        public AppCatalog(string dataDir)
        {
            dir = dataDir;
            Info = new SessionWriter(dataDir).LoadAppInfo();
            foreach (var p in ReadTsv("apps.tsv"))
                if (p.Length >= 4) apps[p[0]] = new AppEntry { CustomName = Empty(p[1]), GroupId = Empty(p[2]), Hidden = p[3] == "1", Favorite = p.Length >= 5 && p[4] == "1" };
            foreach (var p in ReadTsv("groups.tsv"))
                if (p.Length >= 3) groups[p[0]] = new GroupEntry { Name = p[1], Hidden = p[2] == "1", Favorite = p.Length >= 4 && p[3] == "1" };
        }

        static string Empty(string s) { return string.IsNullOrEmpty(s) ? null : s; }

        IEnumerable<string[]> ReadTsv(string name)
        {
            string f = Path.Combine(dir, name);
            if (!File.Exists(f)) yield break;
            foreach (var line in AtomicFile.ReadAllLinesShared(f))
            {
                if (line.Length == 0 || line.StartsWith("V\t")) continue;
                yield return line.Split('\t');
            }
        }

        // ---------- 조회 ----------

        /// <summary>앱 ID → 집계 키 (묶였으면 묶음 키).</summary>
        public string KeyOf(string appId)
        {
            AppEntry e;
            if (apps.TryGetValue(appId, out e) && e.GroupId != null && groups.ContainsKey(e.GroupId)) return GroupPrefix + e.GroupId;
            return appId;
        }

        public static bool IsGroup(string key) { return key.StartsWith(GroupPrefix, StringComparison.Ordinal); }

        /// <summary>원래 이름 (묶음은 묶음 이름).</summary>
        public string OriginalName(string key)
        {
            if (IsGroup(key)) { GroupEntry g; return groups.TryGetValue(key.Substring(GroupPrefix.Length), out g) ? g.Name : key; }
            AppInfo a;
            return Info.TryGetValue(key, out a) ? a.Name : key;
        }

        /// <summary>표시 이름: 사용자 이름 → 원래 이름.</summary>
        public string DisplayName(string key)
        {
            if (!IsGroup(key)) { AppEntry e; if (apps.TryGetValue(key, out e) && e.CustomName != null) return e.CustomName; }
            return OriginalName(key);
        }

        public bool IsHidden(string key)
        {
            if (IsGroup(key)) { GroupEntry g; return groups.TryGetValue(key.Substring(GroupPrefix.Length), out g) && g.Hidden; }
            AppEntry e; return apps.TryGetValue(key, out e) && e.Hidden;
        }

        /// <summary>색 슬롯: appinfo.tsv에 처음 기록된 순서 (묶음은 가장 먼저 기록된 멤버).</summary>
        public int SlotOf(string key)
        {
            if (slots == null)
            {
                slots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var id in Info.Keys) slots[id] = slots.Count;
            }
            if (IsGroup(key))
            {
                int best = int.MaxValue;
                foreach (var m in Members(key)) { int s; if (slots.TryGetValue(m, out s) && s < best) best = s; }
                return best == int.MaxValue ? 0 : best;
            }
            int v;
            return slots.TryGetValue(key, out v) ? v : Math.Abs(key.GetHashCode());
        }
        Dictionary<string, int> slots;

        public List<string> Members(string groupKey)
        {
            var list = new List<string>();
            string gid = groupKey.Substring(GroupPrefix.Length);
            foreach (var kv in apps) if (string.Equals(kv.Value.GroupId, gid, StringComparison.OrdinalIgnoreCase)) list.Add(kv.Key);
            return list;
        }

        // ---------- 변경 ----------

        AppEntry Entry(string appId)
        {
            AppEntry e;
            if (!apps.TryGetValue(appId, out e)) { e = new AppEntry(); apps[appId] = e; }
            return e;
        }

        /// <summary>이름 바꾸기. 빈 이름이면 원래 이름으로 돌아간다. 묶음은 묶음 이름을 바꾼다.</summary>
        public void Rename(string key, string name)
        {
            name = string.IsNullOrWhiteSpace(name) ? null : Clean(name.Trim());
            if (IsGroup(key)) { GroupEntry g; if (groups.TryGetValue(key.Substring(GroupPrefix.Length), out g) && name != null) g.Name = name; }
            else Entry(key).CustomName = name;
        }

        public bool IsFavorite(string key)
        {
            if (IsGroup(key)) { GroupEntry g; return groups.TryGetValue(key.Substring(GroupPrefix.Length), out g) && g.Favorite; }
            AppEntry e; return apps.TryGetValue(key, out e) && e.Favorite;
        }

        public void SetFavorite(string key, bool favorite)
        {
            if (IsGroup(key)) { GroupEntry g; if (groups.TryGetValue(key.Substring(GroupPrefix.Length), out g)) g.Favorite = favorite; }
            else Entry(key).Favorite = favorite;
        }

        public void SetHidden(string key, bool hidden)
        {
            if (IsGroup(key)) { GroupEntry g; if (groups.TryGetValue(key.Substring(GroupPrefix.Length), out g)) g.Hidden = hidden; }
            else Entry(key).Hidden = hidden;
        }

        /// <summary>여러 키(앱 또는 묶음)를 하나로 묶는다. 새 묶음 키를 돌려준다.</summary>
        public string Merge(IEnumerable<string> keys, string name)
        {
            string gid = DateTime.Now.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
            groups[gid] = new GroupEntry { Name = Clean(name) };
            foreach (var k in keys)
            {
                if (IsGroup(k)) { foreach (var m in Members(k)) Entry(m).GroupId = gid; groups.Remove(k.Substring(GroupPrefix.Length)); }
                else Entry(k).GroupId = gid;
            }
            return GroupPrefix + gid;
        }

        /// <summary>묶음을 푼다. 멤버 앱들은 원래대로 따로 집계된다.</summary>
        public void Unmerge(string groupKey)
        {
            string gid = groupKey.Substring(GroupPrefix.Length);
            foreach (var m in Members(groupKey)) Entry(m).GroupId = null;
            groups.Remove(gid);
        }

        public void Save()
        {
            var sb = new StringBuilder("V\t1\r\n");
            foreach (var kv in apps)
                if (kv.Value.CustomName != null || kv.Value.GroupId != null || kv.Value.Hidden || kv.Value.Favorite)
                    sb.Append(kv.Key).Append('\t').Append(kv.Value.CustomName ?? "").Append('\t').Append(kv.Value.GroupId ?? "")
                      .Append('\t').Append(kv.Value.Hidden ? "1" : "0").Append('\t').Append(kv.Value.Favorite ? "1" : "0").Append("\r\n");
            AtomicFile.WriteAllText(Path.Combine(dir, "apps.tsv"), sb.ToString());

            sb = new StringBuilder("V\t1\r\n");
            foreach (var kv in groups)
                sb.Append(kv.Key).Append('\t').Append(kv.Value.Name).Append('\t').Append(kv.Value.Hidden ? "1" : "0")
                  .Append('\t').Append(kv.Value.Favorite ? "1" : "0").Append("\r\n");
            AtomicFile.WriteAllText(Path.Combine(dir, "groups.tsv"), sb.ToString());
        }

        static string Clean(string s) { return (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' '); }
    }
}
