using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DeveloperIDE;

/// <summary>IDE 里的一个插件项目 = 磁盘上一个目录 + 几个约定文件。</summary>
public sealed class PluginProject
{
    /// <summary>编译产物固定叫 Plugin.dll，清单里的 entry 与它一致。</summary>
    public const string AssemblyName = "Plugin";

    /// <summary>宿主清单校验的 id 规则：字母数字开头，允许 . _ -，长度 1~64。</summary>
    public const int MaxIdLength = 64;

    public required string DisplayName { get; init; }
    public required string Id { get; init; }
    public required string CsNamespace { get; init; }
    public required string ProjectDir { get; init; }

    /// <summary>源文件名，打开别人的项目时可能是 HelloPlugin.cs 之类的名字。</summary>
    public string SourceFileName { get; set; } = "Plugin.cs";

    /// <summary>打开项目时读到的原 plugin.json 文本；重打包时按字段合并，不覆盖作者的 version / author。</summary>
    public string? LoadedManifest { get; set; }

    public string SourcePath => Path.Combine(ProjectDir, SourceFileName);
    public string ManifestPath => Path.Combine(ProjectDir, "plugin.json");
    public string BuildDir => Path.Combine(ProjectDir, "build");
    public string DllPath => Path.Combine(BuildDir, AssemblyName + ".dll");
    public string PackagePath => Path.Combine(ProjectDir, Id + ".cblplugin");

    /// <summary>IDE 生成项目的根目录（exe 旁 projects\，便携）。</summary>
    public static string ProjectsRoot => Path.Combine(AppContext.BaseDirectory, "projects");
}

public sealed record BuildResult(bool Ok, byte[]? Assembly, string? EntryType, string? Error)
{
    public static BuildResult Fail(string error) => new(false, null, null, error);
}

/// <summary>
/// 插件工作区：建项目 / 生成模板 / Roslyn 内存编译 / 打 .cblplugin 包 / 部署到主程序。
///
/// 与宿主保持一致的三条硬约定：
///   · 包扩展名 .cblplugin（PluginInstaller.PackageExtension），清单在压缩包根；
///   · plugin.json 的 entry 只能是文件名，type 必须是 public 且实现 IPlugin；
///   · 契约程序集不进包（宿主默认上下文提供，插件带一份会导致 IPlugin 类型对不上）。
/// </summary>
public static class PluginWorkshop
{
    public const string ContractAssemblyName = "ClassDailyLand2.Plugin.Abstractions";
    public const string PackageExtension = ".cblplugin";

    const int HostApiVersion = 1;

    static readonly JsonSerializerOptions ManifestOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ─────────────────────────── 项目 ───────────────────────────

    public static PluginProject Create(string displayName, Action<string>? log = null)
    {
        var id = MakeId(displayName);
        var ns = MakeIdentifier(displayName);
        var dir = Path.Combine(PluginProject.ProjectsRoot, id);
        Directory.CreateDirectory(dir);

        var project = new PluginProject
        {
            DisplayName = displayName,
            Id = id,
            CsNamespace = ns,
            ProjectDir = dir,
        };

        // 已存在就不覆盖源码，免得把上次改到一半的插件清空
        if (!File.Exists(project.SourcePath))
            File.WriteAllText(project.SourcePath, Template(ns, displayName));

        File.WriteAllText(project.ManifestPath, ManifestJson(project, DefaultEntryType(project)));
        log?.Invoke($"项目已创建：{dir}");
        return project;
    }

    /// <summary>
    /// 在任意目录建项目（新建弹窗走这里）：源码用插件名当命名空间，
    /// 清单里的 id / name / author / version 全按用户填的写，entry 固定是 Plugin.dll，
    /// type 是「命名空间.Main」。清单用 System.Text.Json 序列化并带缩进。
    /// </summary>
    public static PluginProject CreateAt(string directory, string displayName,
                                         string? author = null, string? version = null,
                                         Action<string>? log = null)
    {
        var dir = Path.GetFullPath(directory);
        Directory.CreateDirectory(dir);

        var project = new PluginProject
        {
            DisplayName = displayName,
            Id = MakeId(displayName),
            CsNamespace = MakeIdentifier(displayName),
            ProjectDir = dir,
        };

        File.WriteAllText(project.SourcePath, Template(project.CsNamespace, displayName, author, version));

        var json = ManifestJson(project, DefaultEntryType(project), author, version);
        File.WriteAllText(project.ManifestPath, json);
        project.LoadedManifest = json;

        log?.Invoke($"项目已创建：{dir}");
        return project;
    }

    /// <summary>取已存在的项目目录（新建重名时复用）。</summary>
    public static PluginProject? Open(string displayName)
    {
        var id = MakeId(displayName);
        var dir = Path.Combine(PluginProject.ProjectsRoot, id);
        if (!Directory.Exists(dir)) return null;

        var ns = MakeIdentifier(displayName);
        var project = new PluginProject
        {
            DisplayName = displayName,
            Id = id,
            CsNamespace = ns,
            ProjectDir = dir,
        };
        if (!File.Exists(project.SourcePath)) File.WriteAllText(project.SourcePath, Template(ns, displayName));
        return project;
    }

    /// <summary>
    /// 默认模板。<c>author</c> / <c>version</c> 写进类头注释，
    /// 入口类固定叫 <c>Main</c> —— 清单里的 type 就是「命名空间.Main」，与新建弹窗保持一致。
    /// </summary>
    static string Template(string ns, string displayName, string? author = null, string? version = null)
    {
        var who = string.IsNullOrWhiteSpace(author) ? "未署名" : author!;
        var ver = string.IsNullOrWhiteSpace(version) ? "1.0.0" : version!;

        return $$"""
        using ClassDailyLand2.Plugin;

        namespace {{ns}};

        /// <summary>
        /// {{displayName}} —— 由 DeveloperIDE 生成的插件入口。
        /// 作者：{{who}}　版本：{{ver}}
        ///
        /// 纪律：
        ///   · Initialize / Shutdown 只在 UI 线程调用，别在这里做 IO 或网络；
        ///   · 注册点返回的 id 要留着，卸载时 Unregister；
        ///   · 每秒取文案的回调里只读字段，不要 new 大对象。
        /// </summary>
        public sealed class Main : IPlugin
        {
            IPluginHost? _host;
            string? _islandText;

            public void Initialize(IPluginHost host)
            {
                _host = host;
                host.Log("{{displayName}} 已加载，宿主版本 " + host.HostVersion);

                // 主岛文案后缀：一段随时可变的短文本
                _islandText = host.RegisterIslandText(new IslandTextSpec(() => "{{displayName}}", Priority: 0));
            }

            public void Shutdown()
            {
                if (_host is null) return;
                if (_islandText is not null) _host.Unregister(_islandText);
                _host.Log("{{displayName}} 已卸载");
                _host = null;
            }
        }

        """;
    }

    /// <summary>清单里的入口类型约定：命名空间 + Main（与模板里的类名一致）。</summary>
    public static string DefaultEntryType(PluginProject project) => project.CsNamespace + ".Main";

    /// <summary>类型名里的命名空间段逐段清洗后拼回（Foo.Bar.Main → Foo.Bar）。</summary>
    public static string MakeNamespace(string dotted)
    {
        var parts = (dotted ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "MyPlugin";
        return string.Join('.', parts.Select(MakeIdentifier));
    }

    public static string ManifestJson(PluginProject project, string? entryType,
                                      string? author = null, string? version = null)
    {
        var type = string.IsNullOrWhiteSpace(entryType) ? DefaultEntryType(project) : entryType!;

        // 打开别人已有项目：以原清单为底，只改 entry / type，别把作者的 version / author / description 洗掉
        if (!string.IsNullOrWhiteSpace(project.LoadedManifest))
        {
            try
            {
                var node = JsonNode.Parse(project.LoadedManifest!) as JsonObject;
                if (node is not null)
                {
                    node["entry"] = PluginProject.AssemblyName + ".dll";
                    node["type"] = type;
                    if (!string.IsNullOrWhiteSpace(author)) node["author"] = author!.Trim();
                    if (!string.IsNullOrWhiteSpace(version)) node["version"] = version!.Trim();
                    if (!node.ContainsKey("id")) node["id"] = project.Id;
                    if (!node.ContainsKey("apiVersion")) node["apiVersion"] = HostApiVersion;
                    return node.ToJsonString(ManifestOptions);
                }
            }
            catch (JsonException)
            {
                // 原清单坏掉就整个重写一份干净的
            }
        }

        var manifest = new Dictionary<string, object?>
        {
            ["id"] = project.Id,
            ["name"] = project.DisplayName,
            ["version"] = string.IsNullOrWhiteSpace(version) ? "1.0.0" : version!.Trim(),
            ["description"] = project.DisplayName + "（DeveloperIDE 生成）",
            ["author"] = string.IsNullOrWhiteSpace(author) ? Environment.UserName : author!.Trim(),
            ["entry"] = PluginProject.AssemblyName + ".dll",
            ["type"] = type,
            ["apiVersion"] = HostApiVersion,
        };
        return JsonSerializer.Serialize(manifest, ManifestOptions);
    }

    /// <summary>
    /// 打开磁盘上已存在的项目（目录里要有 plugin.json，没有就按目录名建）。
    /// 源码取目录里现成的 .cs（优先 Plugin.cs），一个都没有才生成模板。
    /// </summary>
    public static PluginProject? OpenFrom(string directory, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            log?.Invoke("目录不存在：" + directory);
            return null;
        }

        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var folderName = Path.GetFileName(root);

        var manifestPath = Path.Combine(root, "plugin.json");
        string? manifestText = null;
        var id = "";
        var name = "";
        var typeHint = "";

        if (File.Exists(manifestPath))
        {
            try
            {
                manifestText = File.ReadAllText(manifestPath);
                using var doc = JsonDocument.Parse(manifestText);
                id = ReadString(doc.RootElement, "id");
                name = ReadString(doc.RootElement, "name");
                typeHint = ReadString(doc.RootElement, "type");
            }
            catch (JsonException ex)
            {
                log?.Invoke("plugin.json 解析失败，按目录名建项目：" + ex.Message);
            }
            catch (IOException ex)
            {
                log?.Invoke("读不了 plugin.json：" + ex.Message);
            }
        }
        else
        {
            log?.Invoke("目录里没有 plugin.json，按目录名生成一份：" + folderName);
        }

        var project = new PluginProject
        {
            DisplayName = name.Length > 0 ? name : folderName,
            Id = id.Length > 0 ? id : MakeId(folderName),
            // 类型名 Foo.Bar.Plugin 里的 Foo.Bar 就是命名空间；拿不到就按显示名推
            CsNamespace = typeHint.Contains('.')
                ? MakeNamespace(typeHint[..typeHint.LastIndexOf('.')])
                : MakeIdentifier(name.Length > 0 ? name : folderName),
            ProjectDir = root,
            LoadedManifest = manifestText,
        };

        project.SourceFileName = PickSourceFile(root, log) ?? "Plugin.cs";
        if (!File.Exists(project.SourcePath))
        {
            project.SourceFileName = "Plugin.cs";
            File.WriteAllText(project.SourcePath, Template(project.CsNamespace, project.DisplayName));
            log?.Invoke("目录里没有 C# 源码，已生成模板：" + project.SourcePath);
        }
        else
        {
            log?.Invoke("已打开项目：" + root + $"（id={project.Id}，源码={project.SourceFileName}）");
        }
        return project;
    }

    /// <summary>优先 Plugin.cs；否则取目录顶层第一个非 AssemblyInfo 的 .cs（不碰 bin\ 与 obj\）。</summary>
    static string? PickSourceFile(string root, Action<string>? log)
    {
        var preferred = Path.Combine(root, "Plugin.cs");
        if (File.Exists(preferred)) return "Plugin.cs";

        try
        {
            var found = Directory.EnumerateFiles(root, "*.cs", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .FirstOrDefault(name => name is not null &&
                                        !name.Equals("AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase));
            if (found is not null) log?.Invoke("源码用目录里现成的文件：" + found);
            return found;
        }
        catch (Exception ex)
        {
            log?.Invoke("列目录失败：" + ex.Message);
            return null;
        }
    }

    static string ReadString(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? "").Trim()
            : "";


    // ─────────────────────────── 命名清洗 ───────────────────────────

    /// <summary>
    /// 插件显示名 → 清单 id。宿主只接受 ASCII 的字母数字与 . _ -（<c>^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$</c>），
    /// 中文名一律转成 plugin-xxxxxx 形式的稳定后缀，避免两个中文插件撞 id。
    /// </summary>
    public static string MakeId(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in (name ?? "").Trim())
        {
            var ascii = ch < 128 && (char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-');
            sb.Append(ascii ? ch : '-');
        }

        var id = sb.ToString().Trim('-', '.', '_');

        // 只剩分隔符（纯中文 / 纯空格）→ 退回 "plugin" + 名字的稳定哈希
        if (id.Length == 0) id = "plugin-" + StableSuffix(name ?? "");
        if (!char.IsLetterOrDigit(id[0])) id = "p" + id;
        return id.Length > PluginProject.MaxIdLength ? id[..PluginProject.MaxIdLength] : id;
    }

    /// <summary>名字里的非 ASCII 字符不能进 id，用 FNV-1a 的 6 位 base36 保证同名同 id、异名不同 id。</summary>
    static string StableSuffix(string text)
    {
        var hash = 2166136261u;
        foreach (var ch in text)
        {
            hash ^= ch;
            hash *= 16777619u;
        }
        return hash.ToString("x8")[..6];
    }

    /// <summary>插件显示名 → 合法 C# 命名空间（中文等非标识符字符换成下划线）。</summary>
    public static string MakeIdentifier(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in (name ?? "").Trim())
            sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');

        var id = sb.ToString().Trim('_');
        if (id.Length == 0) id = "MyPlugin";
        if (char.IsDigit(id[0])) id = "_" + id;
        return id;
    }

    // ─────────────────────────── 契约定位 ───────────────────────────

    /// <summary>
    /// 找契约 dll：先取 IDE 输出目录那份（ProjectReference 复制过来的），
    /// 再顺着目录树往上找契约工程的 bin（改了契约但没重编 IDE 时也能用）。
    /// </summary>
    public static string? FindContract()
    {
        foreach (var candidate in ContractCandidates())
        {
            try
            {
                var full = Path.GetFullPath(candidate);
                if (File.Exists(full)) return full;
            }
            catch { /* 非法路径直接跳过 */ }
        }
        return null;
    }

    static IEnumerable<string> ContractCandidates()
    {
        var file = ContractAssemblyName + ".dll";
        yield return Path.Combine(AppContext.BaseDirectory, file);

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 6 && dir is not null; depth++, dir = dir.Parent)
        {
            foreach (var config in new[] { "Debug", "Release" })
            {
                foreach (var tfm in new[] { "net10.0-windows10.0.19041.0", "net10.0-windows", "net10.0" })
                {
                    yield return Path.Combine(dir.FullName,
                        "ClassDailyLand2.Plugin.Abstractions", "bin", config, tfm, file);
                }
            }
            yield return Path.Combine(dir.FullName, file);
        }
    }

    // ─────────────────────────── 编译 ───────────────────────────

    /// <summary>编译 + 打包；诊断全部打到控制台回调里。</summary>
    public static BuildResult BuildAndPack(PluginProject project, Action<string> log)
    {
        var contract = FindContract();
        if (contract is null)
        {
            log("找不到契约程序集 " + ContractAssemblyName + ".dll —— 先编译一次契约工程（或主工程）。");
            return BuildResult.Fail("缺少契约程序集");
        }

        if (!File.Exists(project.SourcePath))
        {
            log("源码不存在：" + project.SourcePath);
            return BuildResult.Fail("缺少源码");
        }

        var references = CollectReferences(contract, out var refCount, log);
        log($"引用 {refCount} 个程序集（契约：{Path.GetFileName(contract)}）");

        var source = File.ReadAllText(project.SourcePath);
        var tree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Latest),
            path: project.SourcePath,
            encoding: Encoding.UTF8);

        var compilation = CSharpCompilation.Create(
            PluginProject.AssemblyName,
            new[] { tree },
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                nullableContextOptions: NullableContextOptions.Enable));

        var started = Stopwatch.StartNew();
        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        started.Stop();

        if (!emit.Success)
        {
            foreach (var diagnostic in emit.Diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error))
                Report(log, diagnostic);
            log($"编译失败（{started.ElapsedMilliseconds} ms）");
            return BuildResult.Fail("编译失败");
        }

        // 成功时只报警告，错误分支上面已经报完
        foreach (var diagnostic in compilation.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Warning))
            Report(log, diagnostic);

        var entryType = FindEntryType(compilation, tree);
        if (entryType is null)
            log("警告：没有找到实现 IPlugin 的 public 类，装上去宿主会报「找不到入口类型」。");

        var bytes = ms.ToArray();
        log($"编译通过（{started.ElapsedMilliseconds} ms）→ {PluginProject.AssemblyName}.dll，{bytes.Length / 1024.0:F1} KB");

        Pack(project, bytes, entryType, log);
        return new BuildResult(true, bytes, entryType, null);
    }

    /// <summary>
    /// 引用来源：当前进程已加载的程序集 + 运行时可信程序集清单（TPA）+ 契约 dll。
    /// 按简单名去重 —— 同一个程序集被引用两次 Roslyn 会直接报错。
    /// </summary>
    static List<MetadataReference> CollectReferences(string contract, out int count, Action<string> log)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<MetadataReference>();

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string full;
            try { full = Path.GetFullPath(path); }
            catch { return; }
            if (!full.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return;
            if (!seen.Add(Path.GetFileNameWithoutExtension(full))) return;
            if (!File.Exists(full)) return;
            try { list.Add(MetadataReference.CreateFromFile(full)); }
            catch (Exception ex) { log($"引用跳过 {Path.GetFileName(full)}：{ex.Message}"); }
        }

        // 契约放最前：同名时以 IDE 认到的这份为准
        Add(contract);

        // ★ 本工程是单文件发布：Assembly.Location 恒为空字符串（读它只会得到 IL3000 警告），
        //   所以引用不从"已加载程序集的位置"取，改从两个可靠来源：
        //   ① 程序目录里现成的 dll（framework-dependent / 单文件都一样）；
        //   ② 运行时给的 TPA 清单（框架程序集都在里面）。
        var baseDir = AppContext.BaseDirectory;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                if (assembly.IsDynamic) continue;

                var name = assembly.GetName().Name;
                if (string.IsNullOrEmpty(name)) continue;

                Add(Path.Combine(baseDir, name + ".dll"));
            }
            catch { /* 拿不到名字的程序集忽略 */ }
        }

        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
        {
            foreach (var entry in tpa.Split(Path.PathSeparator))
                Add(entry.Trim());
        }

        count = list.Count;
        return list;
    }

    /// <summary>找 plugin.json 该写哪个类型名（第一个 public 且实现契约 IPlugin 的类）。</summary>
    static string? FindEntryType(CSharpCompilation compilation, SyntaxTree tree)
    {
        var model = compilation.GetSemanticModel(tree);

        foreach (var cls in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (cls.Modifiers.IndexOf(SyntaxKind.PublicKeyword) < 0) continue;
            if (cls.Modifiers.IndexOf(SyntaxKind.AbstractKeyword) >= 0) continue;
            if (cls.Modifiers.IndexOf(SyntaxKind.StaticKeyword) >= 0) continue;

            if (model.GetDeclaredSymbol(cls) is not INamedTypeSymbol symbol) continue;
            if (symbol.IsAbstract) continue;

            foreach (var face in symbol.AllInterfaces)
            {
                if (!string.Equals(face.Name, "IPlugin", StringComparison.Ordinal)) continue;
                if (!string.Equals(face.ContainingAssembly?.Name, ContractAssemblyName, StringComparison.OrdinalIgnoreCase)) continue;

                return symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                             .Replace("global::", "", StringComparison.Ordinal);
            }
        }
        return null;
    }

    static void Report(Action<string> log, Diagnostic diagnostic)
    {
        var span = diagnostic.Location.GetLineSpan();
        var file = string.IsNullOrEmpty(span.Path) ? "" : Path.GetFileName(span.Path);
        var where = file.Length == 0 ? "" : $"{file}:{span.StartLinePosition.Line + 1}:{span.StartLinePosition.Character + 1} ";
        log($"{diagnostic.Severity} {diagnostic.Id}: {where}{diagnostic.GetMessage()}");
    }

    // ─────────────────────────── 打包 ───────────────────────────

    /// <summary>build\ 里只放 Plugin.dll 与 plugin.json，整目录压成 .cblplugin。</summary>
    public static string Pack(PluginProject project, byte[] assembly, string? entryType, Action<string> log)
    {
        // 每次重建 staging：留旧文件会被一起打进包
        if (Directory.Exists(project.BuildDir))
        {
            try { Directory.Delete(project.BuildDir, recursive: true); }
            catch (Exception ex) { log($"清理 build 目录失败（稍后可能覆盖）：{ex.Message}"); }
        }
        Directory.CreateDirectory(project.BuildDir);

        File.WriteAllBytes(project.DllPath, assembly);
        var json = ManifestJson(project, entryType);
        File.WriteAllText(Path.Combine(project.BuildDir, "plugin.json"), json);

        // 项目根那份一并刷新，方便直接改版本号
        File.WriteAllText(project.ManifestPath, json);

        if (File.Exists(project.PackagePath))
        {
            try { File.Delete(project.PackagePath); }
            catch (Exception ex) { log($"旧包删除失败：{ex.Message}"); }
        }

        ZipFile.CreateFromDirectory(project.BuildDir, project.PackagePath,
            CompressionLevel.Optimal, includeBaseDirectory: false);

        var size = new FileInfo(project.PackagePath).Length;
        log($"打包：{Path.GetFileName(project.PackagePath)}（{size / 1024.0:F1} KB，清单在压缩包根）");
        return project.PackagePath;
    }

    // ─────────────────────────── 部署与联调 ───────────────────────────

    /// <summary>主程序同级的 plugins\ 目录（宿主 DataPaths 的插件根）。</summary>
    public static string PluginsRootOf(string mainExe) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(mainExe)) ?? "", "plugins");

    public static string PluginTargetDir(string mainExe, string pluginId) =>
        Path.Combine(PluginsRootOf(mainExe), pluginId);

    /// <summary>主程序进程还在跑时不能替换 dll —— 文件被锁，宿主也会加载到半份新代码。</summary>
    public static bool IsMainAppRunning(string mainExe)
    {
        // 纯 P/Invoke（Toolhelp 快照）：Process.GetProcessesByName 在权限不足时会抛 Win32Exception，
        // 而 IDE 每次部署插件都要问一次，异常会一直刷在调试输出里。
        try
        {
            return ClassDailyLand2.Shared.NativeProcess.AnyRunning(Path.GetFileName(mainExe));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// 只覆盖 Plugin.dll 与 plugin.json，绝不动插件目录里的 data\（那是插件自己的私有存储）。
    /// </summary>
    public static string Deploy(PluginProject project, string mainExe, Action<string> log)
    {
        var target = PluginTargetDir(mainExe, project.Id);
        Directory.CreateDirectory(target);

        foreach (var file in Directory.GetFiles(project.BuildDir))
        {
            var dest = Path.Combine(target, Path.GetFileName(file));
            File.Copy(file, dest, overwrite: true);
            log($"→ {dest}");
        }
        return target;
    }

    public static void Launch(string mainExe, Action<string> log)
    {
        var info = new ProcessStartInfo
        {
            FileName = mainExe,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(mainExe)) ?? "",
            UseShellExecute = true,
        };
        var process = Process.Start(info);
        log(process is null ? "主程序未能启动" : $"主程序已启动（PID {process.Id}）");
    }
}

/// <summary>ide_config.json —— 主程序路径 + 主题选择，exe 旁，便携。</summary>
public sealed record IdeConfig(string MainAppPath, string? Theme = null)
{
    static string FilePath => Path.Combine(AppContext.BaseDirectory, "ide_config.json");

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static IdeConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<IdeConfig>(File.ReadAllText(FilePath), Options);
                if (loaded is not null) return loaded;
            }
        }
        catch { /* 坏配置按没配处理 */ }
        return new IdeConfig("");
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options)); }
        catch { /* 存不下也不影响本次使用 */ }
    }
}
