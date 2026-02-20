using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace AnalyzePj
{
    /// <summary>
    /// ActionController を解析し、リクエストパラメータ内に enum を含むアクションを探す（Project/Controller 単位で集約）
    /// </summary>
    public sealed class AnalyzeActionController
    {
        private readonly Solution _solution;
        private readonly IProgress<string> _log;

        // 型名の表示フォーマット（global::は付けない）
        private static readonly SymbolDisplayFormat TypeFormat =
            new SymbolDisplayFormat(
                typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
                genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
                miscellaneousOptions:
                    SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
                    SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

        public AnalyzeActionController(Solution solution, IProgress<string> log = null)
        {
            _solution = solution ?? throw new ArgumentNullException(nameof(solution));
            _log = log;
        }

        /// <summary>
        /// リクエストパラメータに enum を含むアクションメソッドを検索し、Project/Controller 単位で集約して返す
        /// </summary>
        public async Task<IReadOnlyList<ProjectAnalysisResult>> FindEnumRequestParam(CancellationToken cancellationToken = default)
        {
            // projectId -> result
            var projectMap = new Dictionary<ProjectId, ProjectAnalysisResult>();

            foreach (var project in _solution.Projects)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!string.Equals(project.Language, LanguageNames.CSharp, StringComparison.OrdinalIgnoreCase))
                    continue;

                _log?.Report($"[Project] {project.Name}");

                var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                if (compilation == null)
                {
                    _log?.Report($"  - Skip: compilation is null ({project.Name})");
                    continue;
                }

                // Controller 走査
                foreach (var controller in GetAllNamedTypes(compilation.Assembly.GlobalNamespace).Where(IsController))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Action 走査
                    var controllerActions = new List<ActionMethodInfo>();

                    foreach (var method in controller.GetMembers().OfType<IMethodSymbol>().Where(IsActionMethod))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var requestParams = method.Parameters.Where(IsRequestParameter).ToArray();
                        if (requestParams.Length == 0)
                            continue;

                        // enum 情報を「名前（引数名/プロパティパス）付き」で収集
                        var enumItems = new List<EnumParamItem>();

                        foreach (var p in requestParams)
                        {
                            // パラメータごとに visited を分ける（循環参照対策）
                            var visited = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);

                            CollectEnumParamItems(
                                type: p.Type,
                                currentPath: p.Name,              // ここが「enum型パラメータ（プロパティ）名」の起点
                                dest: enumItems,
                                visited: visited,
                                depthLimit: 6);
                        }

                        // 重複除去（同じ enum が同じパスで複数回拾われるケース対策）
                        //enumItems = enumItems
                        //    .GroupBy(x => (x.EnumType, x.Name), StringComparer.Ordinal)
                        //    .Select(g => g.First())
                        //    .OrderBy(x => x.Name, StringComparer.Ordinal)
                        //    .ThenBy(x => x.EnumType, StringComparer.Ordinal)
                        //    .ToList();

                        if (enumItems.Count == 0)
                            continue;

                        controllerActions.Add(new ActionMethodInfo
                        {
                            MethodName = method.Name,
                            ReturnType = method.ReturnType.ToDisplayString(TypeFormat),
                            Parameters = requestParams.Select(p => (p.Name, p.Type.ToDisplayString(TypeFormat))).ToList(),
                            EnumParams = enumItems
                        });
                    }

                    if (controllerActions.Count == 0)
                        continue;

                    // --- 結果へ集約（Project -> Controller -> Actions） ---
                    if (!projectMap.TryGetValue(project.Id, out var projResult))
                    {
                        projResult = new ProjectAnalysisResult
                        {
                            ProjectName = project.Name,
                            ProjectFilePath = project.FilePath ?? string.Empty,
                            Controllers = new List<ControllerInfo>()
                        };
                        projectMap.Add(project.Id, projResult);
                    }

                    var controllerClassName = controller.ToDisplayString(TypeFormat);
                    var controllerFilePath = GetPrimarySourceFilePath(controller);

                    var ctrlResult = projResult.Controllers
                        .FirstOrDefault(c => string.Equals(c.ControllerClassName, controllerClassName, StringComparison.Ordinal));

                    if (ctrlResult == null)
                    {
                        ctrlResult = new ControllerInfo
                        {
                            ControllerClassName = controllerClassName,
                            ControllerSourceFilePath = controllerFilePath,
                            Actions = new List<ActionMethodInfo>()
                        };
                        projResult.Controllers.Add(ctrlResult);
                    }

                    // Action の追加（同名メソッド重複があり得る場合に備え、シグネチャで一意化）
                    foreach (var a in controllerActions)
                    {
                        if (!ctrlResult.Actions.Any(x =>
                                string.Equals(x.MethodName, a.MethodName, StringComparison.Ordinal) &&
                                string.Equals(x.ReturnType, a.ReturnType, StringComparison.Ordinal) &&
                                SameList(x.Parameters, a.Parameters)))
                        {
                            ctrlResult.Actions.Add(a);
                        }
                    }
                }
            }

            // 並び順を整えて返す（任意）
            var ordered = projectMap.Values
                .OrderBy(p => p.ProjectName, StringComparer.Ordinal)
                .Select(p =>
                {
                    p.Controllers = p.Controllers
                        .OrderBy(c => c.ControllerClassName, StringComparer.Ordinal)
                        .Select(c =>
                        {
                            c.Actions = c.Actions
                                .OrderBy(a => a.MethodName, StringComparer.Ordinal)
                                .ToList();
                            return c;
                        })
                        .ToList();
                    return p;
                })
                .ToList();

            return ordered;
        }

        // ----------------------------
        // DTO（要求の構造）
        // ----------------------------

        /// <summary>
        /// プロジェクト情報
        /// </summary>
        public sealed class ProjectAnalysisResult
        {
            public string ProjectName { get; set; }
            public string ProjectFilePath { get; set; }
            public List<ControllerInfo> Controllers { get; set; }
        }

        /// <summary>
        /// コントローラー情報
        /// </summary>
        public sealed class ControllerInfo
        {
            public string ControllerClassName { get; set; }
            public string ControllerSourceFilePath { get; set; }
            public List<ActionMethodInfo> Actions { get; set; }
        }

        /// <summary>
        /// アクションメソッド情報
        /// </summary>
        public sealed class ActionMethodInfo
        {
            public string MethodName { get; set; }
            public string ReturnType { get; set; }
            public List<(string name, string type)> Parameters { get; set; }

            /// <summary>
            /// リクエストパラメータに含まれる enum の型 と、対応する「enum型パラメータ（プロパティ）名」
            /// </summary>
            public List<EnumParamItem> EnumParams { get; set; }
        }

        /// <summary>
        /// enum 型と、その enum が現れた「引数名/プロパティパス」
        /// 例: status / request.Status / request.Filter.Status
        /// </summary>
        public sealed class EnumParamItem
        {
            public string EnumType { get; set; }
            public string Name { get; set; }
        }

        // ----------------------------
        // Controller / Action 判定
        // ----------------------------
        private static bool IsController(INamedTypeSymbol type)
        {
            if (type == null) return false;
            if (type.TypeKind != TypeKind.Class) return false;
            if (type.IsAbstract) return false;

            if (IsDerivedFrom(type, "Microsoft.AspNetCore.Mvc.ControllerBase") ||
                IsDerivedFrom(type, "Microsoft.AspNetCore.Mvc.Controller"))
            {
                return true;
            }

            return type.Name.EndsWith("Controller", StringComparison.Ordinal);
        }

        private static bool IsActionMethod(IMethodSymbol method)
        {
            if (method == null) return false;

            if (method.MethodKind != MethodKind.Ordinary) return false;
            if (method.IsStatic) return false;
            if (method.IsAbstract) return false;
            if (method.DeclaredAccessibility != Microsoft.CodeAnalysis.Accessibility.Public) return false;

            if (HasAttribute(method, "NonActionAttribute")) return false;
            if (method.ExplicitInterfaceImplementations.Length > 0) return false;

            return true;
        }

        private static bool IsRequestParameter(IParameterSymbol p)
        {
            if (p == null) return false;

            // DI 注入は除外
            if (HasAttribute(p, "FromServicesAttribute")) return false;

            // 典型的なコンテキスト系も除外（必要なら増減）
            var fullName = GetFullTypeName(p.Type);
            if (string.Equals(fullName, "System.Threading.CancellationToken", StringComparison.Ordinal)) return false;
            if (string.Equals(fullName, "Microsoft.AspNetCore.Http.HttpContext", StringComparison.Ordinal)) return false;
            if (string.Equals(fullName, "Microsoft.AspNetCore.Http.HttpRequest", StringComparison.Ordinal)) return false;
            if (string.Equals(fullName, "Microsoft.AspNetCore.Http.HttpResponse", StringComparison.Ordinal)) return false;
            if (string.Equals(fullName, "System.Security.Claims.ClaimsPrincipal", StringComparison.Ordinal)) return false;

            return true;
        }

        // ----------------------------
        // enum 収集（パス付き）
        // ----------------------------
        private static void CollectEnumParamItems(
            ITypeSymbol type,
            string currentPath,
            List<EnumParamItem> dest,
            HashSet<ITypeSymbol> visited,
            int depthLimit)
        {
            if (type == null) return;
            if (depthLimit < 0) return;

            // null 許容注釈は無視（同一性ぶれを減らす）
            type = type.WithNullableAnnotation(NullableAnnotation.None);

            // 配列は要素型を見る（パスは変えない：statuses[] でも statuses として扱う）
            if (type is IArrayTypeSymbol arr)
            {
                CollectEnumParamItems(arr.ElementType, currentPath, dest, visited, depthLimit - 1);
                return;
            }

            // enum そのもの
            if (type.TypeKind == TypeKind.Enum)
            {
                dest.Add(new EnumParamItem
                {
                    EnumType = type.ToDisplayString(TypeFormat),
                    Name = currentPath
                });
                return;
            }

            var named = type as INamedTypeSymbol;
            if (named == null)
                return;

            // 循環参照対策（同一型を何度も掘らない）
            if (!visited.Add(named))
                return;

            // Nullable<T>
            if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
                named.TypeArguments.Length == 1)
            {
                CollectEnumParamItems(named.TypeArguments[0], currentPath, dest, visited, depthLimit - 1);
                return;
            }

            // ジェネリック引数（List<Enum> 等）：enum が出ても「名前」は引数名のまま
            foreach (var ta in named.TypeArguments)
            {
                CollectEnumParamItems(ta, currentPath, dest, visited, depthLimit - 1);
            }

            // クラス/構造体/レコード(相当)なら public settable プロパティを走査
            if (named.TypeKind == TypeKind.Class || named.TypeKind == TypeKind.Struct)
            {
                foreach (var prop in EnumeratePublicSettablePropertiesIncludingBase(named))
                {
                    var nextPath = currentPath + "." + prop.Name;
                    CollectEnumParamItems(prop.Type, nextPath, dest, visited, depthLimit - 1);
                }
            }
        }

        private static IEnumerable<IPropertySymbol> EnumeratePublicSettablePropertiesIncludingBase(INamedTypeSymbol type)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                foreach (var p in t.GetMembers().OfType<IPropertySymbol>())
                {
                    if (p.IsStatic) continue;
                    if (p.IsIndexer) continue;
                    if (p.DeclaredAccessibility != Microsoft.CodeAnalysis.Accessibility.Public) continue;

                    // model binding 想定：set 必須（init-only も SetMethod は存在する）
                    if (p.SetMethod == null) continue;

                    yield return p;
                }
            }
        }

        // ----------------------------
        // ユーティリティ
        // ----------------------------
        private static bool SameList(List<(string, string)> a, List<(string, string)> b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (!string.Equals(a[i].Item1, b[i].Item1, StringComparison.Ordinal) || !string.Equals(a[i].Item2, b[i].Item2, StringComparison.Ordinal)) return false;
            }
            return true;
        }

        private static IEnumerable<INamedTypeSymbol> GetAllNamedTypes(INamespaceSymbol ns)
        {
            foreach (var n in ns.GetNamespaceMembers())
            {
                foreach (var t in GetAllNamedTypes(n))
                    yield return t;
            }

            foreach (var t in ns.GetTypeMembers())
            {
                foreach (var x in FlattenType(t))
                    yield return x;
            }
        }

        private static IEnumerable<INamedTypeSymbol> FlattenType(INamedTypeSymbol type)
        {
            yield return type;

            foreach (var nested in type.GetTypeMembers())
            {
                foreach (var x in FlattenType(nested))
                    yield return x;
            }
        }

        private static bool IsDerivedFrom(INamedTypeSymbol type, string baseTypeFullName)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                if (string.Equals(GetFullTypeName(t), baseTypeFullName, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static bool HasAttribute(ISymbol symbol, string attributeName)
        {
            foreach (var a in symbol.GetAttributes())
            {
                var c = a.AttributeClass;
                if (c == null) continue;

                if (string.Equals(c.Name, attributeName, StringComparison.Ordinal) ||
                    string.Equals(c.Name, TrimAttributeSuffix(attributeName), StringComparison.Ordinal) ||
                    string.Equals(c.Name, attributeName + "Attribute", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static string TrimAttributeSuffix(string name)
        {
            const string suffix = "Attribute";
            if (name != null && name.EndsWith(suffix, StringComparison.Ordinal))
                return name.Substring(0, name.Length - suffix.Length);
            return name;
        }

        private static string GetPrimarySourceFilePath(INamedTypeSymbol type)
        {
            var path = type.Locations
                .Where(l => l.IsInSource && l.SourceTree != null)
                .Select(l => l.SourceTree.FilePath)
                .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));

            return path ?? string.Empty;
        }

        private static string GetFullTypeName(ITypeSymbol type)
        {
            return type.ToDisplayString(
                new SymbolDisplayFormat(
                    typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
                    genericsOptions: SymbolDisplayGenericsOptions.None,
                    miscellaneousOptions: SymbolDisplayMiscellaneousOptions.None));
        }
    }

}
