using System.Text;
using System.Text.RegularExpressions;

namespace LiveTranscribeRu;

/// <summary>
/// Исправление расшифровок Whisper, который в русском режиме пишет английские термины
/// кириллицей («кубернетис» вместо Kubernetes). Работает масштабно, без ручного
/// перечисления вариантов:
///   1) кириллическое слово переводится ОБРАТНО в латиницу (транслитерация),
///   2) сравнивается по близости (Левенштейн) с большим списком канонических терминов,
///   3) если близко — подставляется правильное написание.
/// Плюс есть явные правила «Каноничное = вариант1, вариант2» для коротких/особых слов
/// (напр. git), где нечёткое сопоставление рискованно.
/// Словарь — terms.txt рядом с приложением: строки-каноны (по одной) и строки с «=».
/// </summary>
public sealed class TermCorrector
{
    readonly List<(Regex rx, string canonical)> _explicit = new();
    readonly List<(string display, string latin)> _fuzzy = new();
    public int TermCount => _fuzzy.Count + _explicit.Count;

    public static TermCorrector Load(string path, Action<string> log)
    {
        var c = new TermCorrector();
        try
        {
            if (!File.Exists(path)) File.WriteAllText(path, Default);
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;

                if (line.Contains('='))   // явное правило: Каноничное = вариант, вариант
                {
                    int eq = line.IndexOf('=');
                    var canonical = line[..eq].Trim();
                    foreach (var v in line[(eq + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        if (v.Length >= 2)
                            c._explicit.Add((new Regex($@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(v)}(?![\p{{L}}\p{{N}}])",
                                RegexOptions.IgnoreCase | RegexOptions.Compiled), canonical));
                    c.AddFuzzy(canonical);
                }
                else                      // строка-канон (возможно несколько через запятую)
                {
                    foreach (var term in line.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        c.AddFuzzy(term);
                }
            }
            log($"термины: канонов {c._fuzzy.Count}, явных правил {c._explicit.Count}");
        }
        catch (Exception ex) { log($"термины: ошибка ({ex.Message})"); }
        return c;
    }

    void AddFuzzy(string term)
    {
        // в нечёткое сопоставление — только однословные термины из латиницы (≥4 симв.)
        if (term.Contains(' ')) return;
        var latin = term.ToLowerInvariant();
        if (latin.Length >= 4 && latin.All(ch => ch < 128 && (char.IsLetterOrDigit(ch))))
            _fuzzy.Add((term, latin));
    }

    public string Correct(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        foreach (var (rx, canonical) in _explicit) text = rx.Replace(text, canonical);

        if (_fuzzy.Count > 0)
            text = Regex.Replace(text, @"\p{L}+", m =>
            {
                var w = m.Value;
                if (w.Length < 4 || !IsCyrillic(w)) return w;
                var lat = Translit(w.ToLowerInvariant());
                if (lat.Length < 4) return w;

                string? best = null; int bestDist = int.MaxValue;
                foreach (var (display, latin) in _fuzzy)
                {
                    if (Math.Abs(latin.Length - lat.Length) > 2) continue;   // быстрый отсев
                    int d = Levenshtein(lat, latin);
                    if (d < bestDist) { bestDist = d; best = display; }
                }
                if (best is null) return w;

                int allowed = Math.Min(2, Math.Max(1, (int)(Math.Max(lat.Length, best.Length) * 0.3)));
                return bestDist <= allowed ? best : w;
            });

        return text;
    }

    static bool IsCyrillic(string s) => s.Any(ch => ch >= 'А' && ch <= 'я' || ch == 'ё' || ch == 'Ё');

    static readonly Dictionary<char, string> Tr = new()
    {
        ['а']="a",['б']="b",['в']="v",['г']="g",['д']="d",['е']="e",['ё']="e",['ж']="zh",['з']="z",
        ['и']="i",['й']="y",['к']="k",['л']="l",['м']="m",['н']="n",['о']="o",['п']="p",['р']="r",
        ['с']="s",['т']="t",['у']="u",['ф']="f",['х']="h",['ц']="ts",['ч']="ch",['ш']="sh",['щ']="sch",
        ['ъ']="",['ы']="y",['ь']="",['э']="e",['ю']="yu",['я']="ya",
    };

    static string Translit(string cyr)
    {
        var sb = new StringBuilder(cyr.Length + 4);
        foreach (var ch in cyr) sb.Append(Tr.TryGetValue(ch, out var v) ? v : ch.ToString());
        return sb.ToString();
    }

    static int Levenshtein(string a, string b)
    {
        var d = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) d[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            int prev = d[0]; d[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int tmp = d[j];
                d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1), prev + (a[i - 1] == b[j - 1] ? 0 : 1));
                prev = tmp;
            }
        }
        return d[b.Length];
    }

    // Список канонов (нечёткое сопоставление ловит их искажения автоматически) + явные правила
    // для коротких/спорных слов. Дополняйте своими строками.
    const string Default =
@"# Словарь терминов. Просто перечислите канонические названия (по одному в строке).
# Кириллические искажения Whisper («кубернетис» и т.п.) подхватятся автоматически
# по созвучию. Для коротких/спорных слов — строка вида: Каноничное = вариант1, вариант2
# (варианты не должны совпадать с обычными русскими словами, иначе ложные замены).
Git = гит, гид
Hiddify = хидифай, хидифи, хиди фи, хиди-фи, хидище, хидиже, хидыще, хидиш, хедифи
# --- каноны (созвучия ловятся сами) ---
Kubernetes
Docker
Jenkins
GitLab
GitHub
Grafana
Zabbix
Prometheus
Ansible
Terraform
Helm
Nginx
Apache
Traefik
HAProxy
Kafka
RabbitMQ
Redis
MongoDB
PostgreSQL
ClickHouse
Elasticsearch
OpenSearch
Kibana
Loki
Vault
Consul
Istio
Proxmox
VMware
Kubernetes
kubectl
Ubuntu
Debian
CentOS
Selenium
Swagger
Postman
SonarQube
Nexus
Artifactory
Keycloak
Portainer
Rancher
OpenShift
MinIO
WireGuard
OpenVPN
Telegram
Python
Groovy
Jenkinsfile
Prometheus
playbook
namespace
vrunner
oscript
ibcmd
";
}
