using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using System.Text;


namespace AnalyzePj
{
    internal record AnalyzeResult
    {
        public int Status { get; init; }

        public string? Output { get; init; }
    }

    internal sealed class Analyzer : IDisposable
    {
        private static readonly object _msbuildLock = new object();
        private static bool _msbuildRegistered;

        private readonly MSBuildWorkspace _workspace;
        private readonly IProgress<string>? _log;

        public Analyzer(IProgress<string>? log = null)
        {
            _log = log;

            EnsureMSBuildRegistered(_log);

            // 解析対象のプロジェクトに合わせて必要なら調整
            var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Configuration"] = "Debug",
                ["Platform"] = "Any CPU",
            };

            _workspace = MSBuildWorkspace.Create(properties);

            // 参照先プロジェクトのメタ情報も取り込みたい場合
            _workspace.LoadMetadataForReferencedProjects = true;

            // ロード中の警告や失敗(評価/ビルドの問題など)を拾う
            _workspace.RegisterWorkspaceFailedHandler((WorkspaceDiagnosticEventArgs e) =>
            {
                var kind = e.Diagnostic.Kind.ToString();
                _log?.Report($"[WorkspaceFailed:{kind}] {e.Diagnostic.Message}");
            });
        }

        /// <summary>
        /// 指定されたソリューション(.sln)をロードする
        /// </summary>
        public async Task<Solution> LoadSolutionAsync(string solutionPath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(solutionPath))
                throw new ArgumentException("solutionPath is null or empty.", nameof(solutionPath));

            var fullPath = Path.GetFullPath(solutionPath);

            if (!File.Exists(fullPath))
                throw new FileNotFoundException("Solution file not found.", fullPath);

            _log?.Report($"Loading solution: {fullPath}");

            var progress = new ProjectLoadProgressReporter(_log);

            // MSBuildWorkspace が内部で DesignTimeBuild などを走らせます
            var solution = await _workspace.OpenSolutionAsync(fullPath, progress, cancellationToken)
                                           .ConfigureAwait(false);

            _log?.Report($"Loaded: {solution.FilePath}");
            _log?.Report($"Projects: {solution.Projects.Count()}");

            return solution;
        }

        public void Dispose()
        {
            _workspace.Dispose();
        }

        /// <summary>
        /// MSBuild を Roslyn/MSBuildWorkspace が使えるように登録する（プロセス内で1回だけ）
        /// </summary>
        public static void EnsureMSBuildRegistered(IProgress<string>? log = null)
        {
            lock (_msbuildLock)
            {
                if (_msbuildRegistered) return;

                // すでに登録済みなら二重登録しない
                if (MSBuildLocator.IsRegistered)
                {
                    _msbuildRegistered = true;
                    return;
                }

                // Visual Studio / BuildTools の MSBuild を選択（存在すれば最新）
                var instances = MSBuildLocator.QueryVisualStudioInstances().ToArray();

                if (instances.Length > 0)
                {
                    var instance = instances.OrderByDescending(i => i.Version).First();
                    MSBuildLocator.RegisterInstance(instance);
                    log?.Report($"MSBuild registered: {instance.Name} {instance.Version} ({instance.MSBuildPath})");
                }
                else
                {
                    // VS インスタンスが列挙できない環境向けのフォールバック
                    // ※環境によっては RegisterDefaults が例外になる場合があります
                    MSBuildLocator.RegisterDefaults();
                    log?.Report("MSBuild registered: defaults");
                }

                _msbuildRegistered = true;
            }
        }

        private sealed class ProjectLoadProgressReporter : IProgress<ProjectLoadProgress>
        {
            private readonly IProgress<string>? _log;

            public ProjectLoadProgressReporter(IProgress<string>? log) => _log = log;

            public void Report(ProjectLoadProgress value)
            {
                // 表示がうるさければここで間引く/整形してください
                _log?.Report($"[{value.Operation}] {value.FilePath}");
            }
        }






        public static async Task<AnalyzeResult> AnalyzePrj(string projectPath, string? nsPrefix = null)
        {
            //MSBuildLocator.RegisterDefaults();

            //MSBuildLocator.RegisterMSBuildPath("C:\\Program Files\\dotnet\\sdk");

            var result = new StringBuilder();

            using var workspace = MSBuildWorkspace.Create();
            var project = await workspace.OpenProjectAsync(projectPath);
            var compilation = await project.GetCompilationAsync();
            if (compilation == null) return new AnalyzeResult { Status = 2 };

            var allTypes = GetAllNamedTypes(compilation.Assembly.GlobalNamespace);

            var hits =
                allTypes
                    .Where(t => t.TypeKind is TypeKind.Class or TypeKind.Struct) // recordは class/struct に含まれる
                    .Where(t => t.Name.Contains("Request", StringComparison.OrdinalIgnoreCase))
                    .Where(t => string.IsNullOrEmpty(nsPrefix) || t.ContainingNamespace.ToDisplayString().StartsWith(nsPrefix, StringComparison.Ordinal))
                    .Select(t => new
                    {
                        Type = t,
                        EnumSettableProps = t.GetMembers()
                            .OfType<IPropertySymbol>()
                            .Where(p => p.DeclaredAccessibility == Microsoft.CodeAnalysis.Accessibility.Public)
                            .Where(p => p.IsIndexer == false)
                            .Where(p => p.SetMethod is { DeclaredAccessibility: Microsoft.CodeAnalysis.Accessibility.Public })
                            // init-only も SetMethod に入る（IsInitOnly が true になる）
                            .Where(p => IsEnumOrNullableEnum(p.Type))
                            .ToArray()
                    })
                    .Where(x => x.EnumSettableProps.Length > 0)
                    .OrderBy(x => x.Type.ToDisplayString())
                    .ToArray();

            foreach (var x in hits)
            {
                result.AppendLine(x.Type.ToDisplayString());
                foreach (var p in x.EnumSettableProps)
                {
                    var enumType = UnwrapNullable(p.Type);
                    result.AppendLine($"  - {p.Name}: {enumType.ToDisplayString()} (initOnly={p.SetMethod?.IsInitOnly == true})");
                }
            }

            return new AnalyzeResult { Status = 0, Output = result.ToString() };
        }

        private static IEnumerable<INamedTypeSymbol> GetAllNamedTypes(INamespaceSymbol root)
        {
            var stack = new Stack<INamespaceOrTypeSymbol>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                foreach (var m in cur.GetMembers())
                {
                    if (m is INamespaceSymbol ns) stack.Push(ns);
                    else if (m is INamedTypeSymbol nt)
                    {
                        yield return nt;
                        foreach (var nested in nt.GetTypeMembers())
                            stack.Push(nested);
                    }
                }
            }
        }

        private static bool IsEnumOrNullableEnum(ITypeSymbol t)
        {
            var u = UnwrapNullable(t);
            return u.TypeKind == TypeKind.Enum;
        }

        private static ITypeSymbol UnwrapNullable(ITypeSymbol t)
        {
            if (t is INamedTypeSymbol nts &&
                nts.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
                nts.TypeArguments.Length == 1)
            {
                return nts.TypeArguments[0];
            }
            return t;
        }
    }
}
