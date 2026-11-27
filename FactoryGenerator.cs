namespace DeveloperInTheFlow.FactoryGenerator
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using Common.Logging;

    using CSScriptLib;

    using DeveloperInTheFlow.FactoryGenerator.Models;
    using DeveloperInTheFlow.FactoryGenerator.Services;

    using Humanizer;

    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;

    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    using Attribute = DeveloperInTheFlow.FactoryGenerator.Models.Attribute;
    using Project = Microsoft.CodeAnalysis.Project;

    public class FactoryGenerator
    {
        #region Static Fields

        private static readonly ILog Logger = LogManager.GetLogger<FactoryGenerator>();

        #endregion

        #region Fields

        private readonly string[] attributeImportList;

        private readonly ConstructorBuilderService constructorBuilderService;

        private readonly Lazy<IEvaluator> csscriptEvaluator = new(() =>
                                                                  {
                                                                      CSScript.EvaluatorConfig.ReferenceDomainAssemblies = true;
                                                                      return CSScript.Evaluator.ReferenceAssembly(typeof(JObject).Assembly)
                                                                                     .ReferenceAssembly(typeof(System.Linq.Enumerable).Assembly)
                                                                                     .ReferenceAssembly(typeof(System.Text.RegularExpressions.Regex).Assembly);
                                                                  });

        private readonly FieldsBuilderService fieldsBuilderService;

        private readonly bool forceGeneration;

        private readonly GenericTypeBuilderService genericTypeBuilderService;

        private readonly MethodsBuilderService methodsBuilderService;

        private readonly string solutionDirectory;

        private readonly string templateDirectory;

        private readonly string templateHash;

        private readonly string templatePath;

        private readonly ConcurrentDictionary<string, ImmutableHashSet<string>> templateUsingNamespacesByPath = new(StringComparer.OrdinalIgnoreCase);

        private readonly string version = FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductVersion;

        private readonly Workspace workspace;

        private Solution solution;

        #endregion

        #region Constructors and Destructors

        [DebuggerStepThrough]
        public FactoryGenerator(Workspace workspace,
                                Solution solution,
                                IEnumerable<string> attributeImportList,
                                bool writeXmlDoc,
                                string templatePath,
                                bool forceGeneration)
        {
            this.workspace = workspace;
            this.solution = solution;
            // Normalize solution directory once to ensure consistent path handling across all operations
            this.solutionDirectory = Path.GetFullPath(Path.GetDirectoryName(solution.FilePath));

            var importList = attributeImportList as string[] ?? attributeImportList.ToArray();
            this.attributeImportList = importList;

            // Normalize template path: if relative, resolve against executable directory to ensure
            // consistent hash regardless of current working directory
            if (!Path.IsPathRooted(templatePath))
            {
                var exeDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                this.templatePath = Path.GetFullPath(Path.Combine(exeDirectory, templatePath));
            }
            else
            {
                this.templatePath = Path.GetFullPath(templatePath);
            }

            this.templateDirectory = Path.GetDirectoryName(this.templatePath);
            this.forceGeneration = forceGeneration;

            using (var sha1 = new SHA1Managed())
            {
                var templateBytes = File.ReadAllBytes(this.templatePath);
                var hashBytes = sha1.ComputeHash(templateBytes);
                this.templateHash = BitConverter.ToString(hashBytes).Replace("-", string.Empty);
            }

            var parameterSymbolService = new ParameterSymbolBuilderService();
            var argumentBuilderService = new ArgumentsBuilderService(parameterSymbolService);

            this.fieldsBuilderService = new FieldsBuilderService(parameterSymbolService);
            this.genericTypeBuilderService = new GenericTypeBuilderService();
            this.constructorBuilderService = new ConstructorBuilderService(importList, argumentBuilderService);
            this.methodsBuilderService = new MethodsBuilderService(this.genericTypeBuilderService, argumentBuilderService, parameterSymbolService, writeXmlDoc);
        }

        #endregion

        #region Public Methods and Operators

        public async Task ExecuteAsync()
        {
            var chrono = Stopwatch.StartNew();

            var projectDependencyGraph = this.solution.GetProjectDependencyGraph();
            var projectCount = this.solution.Projects.Count();
            var existingFactoriesTasksList = new List<Task<ICollection<string>>>(projectCount);
            var generatedFactoriesTasksList = new List<Task<ICollection<string>>>(projectCount);
            var hashesFile = Path.Combine(this.solutionDirectory, "generatedFactoryHashes.json");

            // Use case-insensitive comparers to handle path casing variations between runs
            Dictionary<string, Dictionary<string, string>> fileHashesPerProject = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(hashesFile) && !this.forceGeneration)
            {
                var loadedHashes = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(hashesFile));
                // Rebuild with case-insensitive comparers at both levels
                foreach (var kvp in loadedHashes)
                {
                    fileHashesPerProject[kvp.Key] = new Dictionary<string, string>(kvp.Value, StringComparer.OrdinalIgnoreCase);
                }
            }

            Dictionary<string, bool> isSdkStyleProjectDict = new Dictionary<string, bool>();

            foreach (var projectId in projectDependencyGraph.GetTopologicallySortedProjects())
            {
                var project = this.solution.GetProject(projectId);
                var compilation = await project.GetCompilationAsync().ConfigureAwait(false);
                var isSdkStyleProject = await IsSdkStyleProjectAsync(project.FilePath).ConfigureAwait(false);
                isSdkStyleProjectDict[project.FilePath] = isSdkStyleProject;

                Logger.InfoFormat("Processing {0}...", project.Name);

                var catalogTask = CatalogGeneratedFactoriesInProjectAsync(compilation);
                var generateFactoriesTask = this.GenerateFactoriesInProjectAsync(compilation, isSdkStyleProject, fileHashesPerProject, project);

                existingFactoriesTasksList.Add(catalogTask);
                generatedFactoriesTasksList.Add(generateFactoriesTask);
            }

            await Task.WhenAll(existingFactoriesTasksList.Cast<Task>().Concat(generatedFactoriesTasksList));

            var existingGeneratedFactoryFilePaths = existingFactoriesTasksList.SelectMany(task => task.Result).ToArray();
            var generatedFactoryFilePaths = generatedFactoriesTasksList.SelectMany(task => task.Result).ToArray();

            var newFactoryFilePaths = generatedFactoryFilePaths.Except(existingGeneratedFactoryFilePaths, StringComparer.InvariantCultureIgnoreCase).ToArray();
            var obsoleteFactoryFilePaths = existingGeneratedFactoryFilePaths.Except(generatedFactoryFilePaths, StringComparer.InvariantCultureIgnoreCase).ToArray();
            this.RemoveObsoleteFactoriesFromSolution(obsoleteFactoryFilePaths, isSdkStyleProjectDict);

            this.workspace.TryApplyChanges(this.solution);

            this.RemoveObsoleteHashes(fileHashesPerProject);

            File.WriteAllText(hashesFile, JsonConvert.SerializeObject(fileHashesPerProject, Formatting.Indented));

            chrono.Stop();

            LogCodeGenerationStatistics(chrono, generatedFactoryFilePaths, obsoleteFactoryFilePaths, newFactoryFilePaths);
        }

        #endregion

        #region Methods

        /// <summary>
        ///     Strips namespace prefixes from a fully-qualified type name (including inside generic
        ///     angle brackets) when that namespace appears in <paramref name="knownNamespaces"/>,
        ///     producing the shortest name that is unambiguous given the file's using directives.
        /// </summary>
        internal static string ShortenTypeName(string typeName,
                                               ICollection<string> knownNamespaces)
        {
            if (string.IsNullOrEmpty(typeName) || knownNamespaces == null || knownNamespaces.Count == 0)
                return typeName;

            // Process generics recursively by splitting on top-level ',' inside '<...>'
            var ltIdx = typeName.IndexOf('<');
            if (ltIdx >= 0)
            {
                // Find the matching '>' by tracking nesting depth. This correctly handles
                // types with a suffix after the closing '>', e.g. 'Option<T>[]' (array of generic).
                var scanDepth = 0;
                var gtIdx = -1;
                for (var i = ltIdx; i < typeName.Length; i++)
                {
                    if (typeName[i] == '<') scanDepth++;
                    else if (typeName[i] == '>')
                    {
                        scanDepth--;
                        if (scanDepth == 0)
                        {
                            gtIdx = i;
                            break;
                        }
                    }
                }

                if (gtIdx < 0)
                    return typeName; // malformed — return as-is

                var outerPart = typeName.Substring(0, ltIdx);
                var innerContent = typeName.Substring(ltIdx + 1, gtIdx - ltIdx - 1);
                var suffix = typeName.Substring(gtIdx + 1); // e.g. "[]" for array types
                var shortenedOuter = ShortenTypeName(outerPart, knownNamespaces);
                // Split inner args at top-level commas only
                var args = new List<string>();
                var depth = 0;
                var start = 0;
                for (var i = 0; i < innerContent.Length; i++)
                {
                    var c = innerContent[i];
                    if (c == '<') depth++;
                    else if (c == '>') depth--;
                    else if (c == ',' && depth == 0)
                    {
                        args.Add(ShortenTypeName(innerContent.Substring(start, i - start).Trim(), knownNamespaces));
                        start = i + 1;
                    }
                }

                args.Add(ShortenTypeName(innerContent.Substring(start).Trim(), knownNamespaces));
                return $"{shortenedOuter}<{string.Join(", ", args)}>{suffix}";
            }

            // No generics — strip the longest matching namespace prefix
            var best = typeName;
            foreach (var ns in knownNamespaces)
            {
                var prefix = ns + ".";
                if (typeName.StartsWith(prefix, StringComparison.Ordinal))
                {
                    var candidate = typeName.Substring(prefix.Length);
                    // Prefer the shortest result (longest namespace prefix wins)
                    if (candidate.Length < best.Length)
                        best = candidate;
                }
            }

            // DEBUG: Log when we fail to shorten a type that looks like it should be shortened
            if (typeName.Contains("IEcuGroupUreaTestingFunctionRoutineControlFeatureService"))
            {
                System.Diagnostics.Debug.WriteLine($"FAILED TO SHORTEN: '{typeName}'");
                System.Diagnostics.Debug.WriteLine($"Looking for prefixes in {knownNamespaces?.Count ?? 0} namespaces");
                if (knownNamespaces != null)
                {
                    foreach (var ns in knownNamespaces.Where(n => n.Contains("ScrFunctions") || n.Contains("Lidec2") || n.Contains("Edc17")))
                    {
                        System.Diagnostics.Debug.WriteLine($"  Checking: '{ns}.'  StartsWith={typeName.StartsWith(ns + ".")}");
                    }
                }
            }

            return best;
        }

        private static async Task<ICollection<string>> CatalogGeneratedFactoriesInProjectAsync(Compilation compilation)
        {
            var generatedFactoriesCatalog = new List<string>(16);

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var syntaxRootNode = await syntaxTree.GetRootAsync();
                var generatedFactoryClassDeclarations =
                    syntaxRootNode.DescendantNodesAndSelf(syntaxNode => !(syntaxNode is ClassDeclarationSyntax))
                                  .OfType<ClassDeclarationSyntax>()
                                  .Where(classDeclarationSyntax => classDeclarationSyntax.AttributeLists.Count > 0 && classDeclarationSyntax.AttributeLists.SelectMany(a => a.Attributes).Any(x =>
                                                                                                                                                                                              {
                                                                                                                                                                                                  var attributeClassName = x.Name.ToString();
                                                                                                                                                                                                  return attributeClassName == "global::System.CodeDom.Compiler.GeneratedCode";
                                                                                                                                                                                              }))
                                  .ToArray();

                if (generatedFactoryClassDeclarations.Any())
                {
                    var generatedFactoryInterfaces = generatedFactoryClassDeclarations.Select(generatedFactoryClassDeclaration => generatedFactoryClassDeclaration.SyntaxTree.FilePath);

                    generatedFactoriesCatalog.AddRange(generatedFactoryInterfaces);
                }
            }

            return generatedFactoriesCatalog;
        }

        /// <summary>
        ///     Recursively collects all distinct containing namespaces from a set of type symbols,
        ///     including generic type arguments. Used to ensure every referenced namespace has a
        ///     corresponding using directive in the generated file.
        /// </summary>
        private static IEnumerable<INamespaceSymbol> CollectNamespacesFromTypes(IEnumerable<ITypeSymbol> types)
        {
            var visited = new HashSet<string>();
            var queue = new Queue<ITypeSymbol>(types);
            while (queue.Count > 0)
            {
                var type = queue.Dequeue();
                if (type == null) continue;

                // Unwrap arrays
                if (type is IArrayTypeSymbol arrayType)
                {
                    queue.Enqueue(arrayType.ElementType);
                    continue;
                }

                var ns = type.ContainingNamespace;
                if (ns != null && !ns.IsGlobalNamespace)
                {
                    var nsName = ns.ToString();
                    if (visited.Add(nsName))
                        yield return ns;
                }

                // Recurse into generic type arguments
                if (type is INamedTypeSymbol named)
                {
                    foreach (var arg in named.TypeArguments)
                        queue.Enqueue(arg);
                }
            }
        }

        private static bool CompareParameters(IParameterSymbol parameter1,
                                              IParameterSymbol parameter2)
        {
            return (parameter1.Name == parameter2.Name);
        }

        private static SyntaxList<UsingDirectiveSyntax> FilterOutUsings(SyntaxList<UsingDirectiveSyntax> usings,
                                                                        string[] usingsToFilterOut)
        {
            for (;;)
            {
                var index = usings.IndexOf(usingDirectiveSyntax => usingDirectiveSyntax.Alias == null && Array.IndexOf(usingsToFilterOut, usingDirectiveSyntax.Name.ToString()) >= 0);
                if (index < 0)
                {
                    return usings;
                }

                usings = usings.RemoveAt(index);
            }
        }

        private static SyntaxList<UsingDirectiveSyntax> FilterTemplateUsings(SyntaxList<UsingDirectiveSyntax> usings,
                                                                             ImmutableHashSet<string> templateUsingNamespaces)
        {
            var filteredUsings = default(SyntaxList<UsingDirectiveSyntax>);
            foreach (var usingDirective in usings)
            {
                if (!IsUsingProvidedByTemplate(usingDirective, templateUsingNamespaces))
                {
                    filteredUsings = filteredUsings.Add(usingDirective);
                }
            }

            return filteredUsings;
        }

        private static Document FindDocumentFromPath(Solution solution,
                                                     string filePath)
        {
            return solution.Projects
                           .Select(project => project.Documents.FirstOrDefault(d => d.FilePath == filePath))
                           .FirstOrDefault(document => document != null);
        }

        private static string GetDeclarationFullName(TypeDeclarationSyntax typeDeclarationSyntax)
        {
            var typeParameterCount = typeDeclarationSyntax.TypeParameterList == null
                                         ? 0
                                         : typeDeclarationSyntax.TypeParameterList.Parameters.Count;
            var fullyQualifiedName = $"{GetDeclarationNamespaceFullName(typeDeclarationSyntax)}.{typeDeclarationSyntax.Identifier.ValueText}{(typeParameterCount == 0
                                                                                                                                                  ? string.Empty
                                                                                                                                                  : $"`{typeParameterCount}")}";

            return fullyQualifiedName;
        }

        private static string GetDeclarationNamespaceFullName(CSharpSyntaxNode typeDeclarationSyntax)
        {
            var namespaceDeclarationSyntax = typeDeclarationSyntax.FirstAncestorOrSelf<BaseNamespaceDeclarationSyntax>();
            return namespaceDeclarationSyntax.Name.ToString();
        }

        private static AttributeSyntax GetFactoryAttributeFromClassDeclaration(ClassDeclarationSyntax concreteClassDeclarationSyntax)
        {
            AttributeSyntax factoryAttribute;
            try
            {
                factoryAttribute = concreteClassDeclarationSyntax.AttributeLists.SelectMany(a => a.Attributes)
                                                                 .SingleOrDefault(a =>
                                                                                  {
                                                                                      var attributeClassFullName = a.Name.NormalizeWhitespace().ToFullString();
                                                                                      return attributeClassFullName.EndsWith("GenerateFactoryAttribute") || attributeClassFullName.EndsWith("GenerateFactory");
                                                                                  });
            }
            catch (InvalidOperationException e)
            {
                throw new InvalidOperationException($"Could not read GenerateFactoryAttribute from {concreteClassDeclarationSyntax} type.", e);
            }

            return factoryAttribute;
        }

        private static string GetFactoryClassGenericName(ClassDeclarationSyntax concreteTypeDeclarationSyntax,
                                                         INamedTypeSymbol factoryInterfaceTypeSymbol)
        {
            var factoryClassName = $"{concreteTypeDeclarationSyntax.Identifier.ValueText}Factory{GetTypeParametersDeclaration(factoryInterfaceTypeSymbol.TypeParameters)}";

            return factoryClassName;
        }

        private static string GetSafeFileName(string identifier)
        {
            return identifier.Contains("<")
                       ? identifier.Substring(0, identifier.IndexOf("<", StringComparison.Ordinal))
                       : identifier;
        }

        private static (ITypeSymbol returnType, IMethodSymbol symbol)[] GetSuitableFactoryInterfaceMethods(INamedTypeSymbol concreteClassTypeSymbol,
                                                                                                           INamedTypeSymbol factoryInterfaceTypeSymbol,
                                                                                                           INamedTypeSymbol[] genericArgumentTypeSymbols)
        {
            return factoryInterfaceTypeSymbol
                   .AllInterfaces
                   .Add(factoryInterfaceTypeSymbol)
                   .SelectMany(i => i.GetMembers().OfType<IMethodSymbol>())
                   .Select(methodSymbol => (returnType: ResolveGenericArgument(factoryInterfaceTypeSymbol, methodSymbol.ReturnType, genericArgumentTypeSymbols), symbol: methodSymbol))
                   .Where(methodSymbol => concreteClassTypeSymbol.AllInterfaces.Select(i => i.OriginalDefinition).Contains(methodSymbol.returnType.OriginalDefinition, SymbolEqualityComparer.Default))
                   .ToArray();
        }

        private static string GetTypeParametersDeclaration(ImmutableArray<ITypeParameterSymbol> typeParameterSymbols)
        {
            var typeParameters = string.Empty;

            if (typeParameterSymbols.Length > 0)
            {
                typeParameters = $"<{string.Join(" ,", typeParameterSymbols.Select(t => t.Name))}>";
            }

            return typeParameters;
        }

        private static string GetXmlDocSafeTypeName(string typeName)
        {
            return typeName.Replace("<", "{").Replace(">", "}");
        }

        private static async Task<bool> IsSdkStyleProjectAsync(string path)
        {
            using var streamReader = new StreamReader(File.OpenRead(path));
            while (true)
            {
                var line = await streamReader.ReadLineAsync().ConfigureAwait(false);
                if (line.Contains("<Project"))
                {
                    return line.Contains("Sdk=\"Microsoft.NET.Sdk");
                }
            }
        }

        private static bool IsTypeDeclarationSyntaxFactoryTarget(ClassDeclarationSyntax classDeclarationSyntax)
        {
            var attributeListSyntaxes = classDeclarationSyntax.AttributeLists;

            return attributeListSyntaxes.Count > 0 &&
                   attributeListSyntaxes.SelectMany(a => a.Attributes).Any(x =>
                                                                           {
                                                                               var attributeClassName = x.Name is QualifiedNameSyntax qualifiedNameSyntax
                                                                                                            ? qualifiedNameSyntax.Right.Identifier.ToString()
                                                                                                            : x.Name.ToString();
                                                                               return attributeClassName == "GenerateFactory" || attributeClassName == "GenerateFactoryAttribute";
                                                                           });
        }

        private static bool IsUsingProvidedByTemplate(UsingDirectiveSyntax usingDirective,
                                                      ImmutableHashSet<string> templateUsingNamespaces)
        {
            if (usingDirective.Alias != null || usingDirective.StaticKeyword.IsKind(SyntaxKind.StaticKeyword))
            {
                return false;
            }

            var namespaceName = usingDirective.Name.ToString().Trim();
            if (namespaceName.StartsWith("global::", StringComparison.Ordinal))
            {
                namespaceName = namespaceName.Substring("global::".Length);
            }

            return templateUsingNamespaces.Contains(namespaceName);
        }

        private static void LogCodeGenerationStatistics(Stopwatch chrono,
                                                        string[] generatedFactoryFilePaths,
                                                        string[] obsoleteFactoryFilePaths,
                                                        string[] newFactoryFilePaths)
        {
            var elapsedTime = TimeSpan.FromMilliseconds(chrono.ElapsedMilliseconds);
            var statisticsFormat = obsoleteFactoryFilePaths.Any() || newFactoryFilePaths.Any()
                                       ? "{0} (+{1}/-{2})"
                                       : "{0}";
            var factoryStatistics = string.Format(statisticsFormat, "factory".ToQuantity(generatedFactoryFilePaths.Length), newFactoryFilePaths.Length, obsoleteFactoryFilePaths.Length);

            Logger.InfoFormat("Generated {0} in {1}.", factoryStatistics, elapsedTime.Humanize(2));
        }

        private static ITypeSymbol ResolveGenericArgument(INamedTypeSymbol factoryInterfaceTypeSymbol,
                                                          ITypeSymbol methodReturnType,
                                                          INamedTypeSymbol[] genericArgumentTypeSymbols)
        {
            if (methodReturnType is INamedTypeSymbol)
            {
                return methodReturnType;
            }

            if (genericArgumentTypeSymbols.Length == 0)
            {
                return methodReturnType;
            }

            var typeArgumentIndex = factoryInterfaceTypeSymbol.TypeArguments.IndexOf(methodReturnType);

            return typeArgumentIndex >= 0
                       ? genericArgumentTypeSymbols[typeArgumentIndex]
                       : methodReturnType;
        }

        private static INamedTypeSymbol ResolveTypeByName(string typeName,
                                                          IEnumerable<string> namespaces,
                                                          Compilation compilation)
        {
            typeName = typeName.Trim();
            var namespacesArray = namespaces as string[] ?? namespaces.ToArray();

            if (typeName.Contains("<"))
            {
                // Nested generic, e.g. EcuGroupFunctionStep<MaintenanceModeCrsStep>
                var openBracketIdx = typeName.IndexOf('<');
                var outerName = typeName.Substring(0, openBracketIdx).Trim();

                // Find matching closing bracket by tracking depth
                var depth = 0;
                var closeBracketIdx = -1;
                for (var i = openBracketIdx; i < typeName.Length; i++)
                {
                    if (typeName[i] == '<') depth++;
                    else if (typeName[i] == '>')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            closeBracketIdx = i;
                            break;
                        }
                    }
                }

                if (closeBracketIdx == -1)
                {
                    Logger.ErrorFormat("Could not find closing bracket for type: {0}", typeName);
                    return null;
                }

                var innerContent = typeName.Substring(openBracketIdx + 1, closeBracketIdx - openBracketIdx - 1).Trim();
                var innerArgNames = SplitTopLevelGenericArguments(innerContent);

                Logger.DebugFormat("Resolving nested generic: '{0}' with inner args: [{1}]", outerName, string.Join(", ", innerArgNames));

                var resolvedInnerArgs = innerArgNames
                                        .Select(arg => ResolveTypeByName(arg, namespacesArray, compilation))
                                        .ToArray();

                if (resolvedInnerArgs.Any(a => a == null))
                {
                    var failedArgs = innerArgNames.Where((name, idx) => resolvedInnerArgs[idx] == null).ToArray();
                    Logger.ErrorFormat("Failed to resolve inner generic arguments for '{0}': {1}", outerName, string.Join(", ", failedArgs));
                    return null;
                }

                var openGenericMetadataName = $"{outerName}`{innerArgNames.Length}";
                Logger.DebugFormat("Looking for open generic: {0}", openGenericMetadataName);

                var openGeneric = namespacesArray
                                  .Select(ns => compilation.GetTypeByMetadataName($"{ns}.{openGenericMetadataName}"))
                                  .FirstOrDefault(t => t != null)
                                  ?? compilation.GetTypeByMetadataName(openGenericMetadataName);

                if (openGeneric == null)
                {
                    Logger.ErrorFormat("Could not find open generic type: {0} in namespaces: {1}", openGenericMetadataName, string.Join(", ", namespacesArray));
                    return null;
                }

                Logger.DebugFormat("Successfully resolved nested generic: {0}", openGeneric.Construct(resolvedInnerArgs).ToString());
                return openGeneric.Construct(resolvedInnerArgs);
            }

            Logger.DebugFormat("Resolving simple type: {0}", typeName);
            var result = namespacesArray
                         .Select(ns => compilation.GetTypeByMetadataName($"{ns}.{typeName}"))
                         .FirstOrDefault(t => t != null)
                         ?? compilation.GetTypeByMetadataName(typeName);

            if (result == null)
            {
                Logger.ErrorFormat("Could not resolve type: '{0}' in namespaces: {1}", typeName, string.Join(", ", namespacesArray));
            }
            else
            {
                Logger.DebugFormat("Successfully resolved: {0}", result.ToString());
            }

            return result;
        }

        private static IMethodSymbol SelectConstructorFromFactoryMethod(IMethodSymbol factoryMethod,
                                                                        INamedTypeSymbol concreteClassSymbol)
        {
            var factoryMethodParameters = factoryMethod.Parameters;
            var instanceConstructors = concreteClassSymbol.InstanceConstructors
                                                          .Where(c => c.DeclaredAccessibility == Accessibility.Public || c.DeclaredAccessibility == Accessibility.Internal)
                                                          .ToArray();

            try
            {
                var matchedInstanceConstructors = instanceConstructors.GroupBy(c => factoryMethodParameters.Select(fmp => fmp.Name)
                                                                                                           .Count(c.Parameters.Select(cp => cp.Name).Contains))
                                                                      .ToArray();
                var selectedGrouping = matchedInstanceConstructors.SingleOrDefault(g => g.Key == factoryMethodParameters.Length);
                if (selectedGrouping == null)
                {
                    selectedGrouping = matchedInstanceConstructors.OrderBy(g => g.Key - factoryMethodParameters.Length).First();
                }

                var selectedConstructor = selectedGrouping.First();

                return selectedConstructor;
            }
            catch (InvalidOperationException e)
            {
                throw new InvalidOperationException($"Could not select a constructor from {concreteClassSymbol.Name} type with the following parameters: ({string.Join(", ", factoryMethodParameters.Select(x => x.Name))}).", e);
            }
        }

        private static string[] SplitTopLevelGenericArguments(string content)
        {
            var args = new List<string>();
            var depth = 0;
            var start = 0;
            for (var i = 0; i < content.Length; i++)
            {
                var c = content[i];
                if (c == '<') depth++;
                else if (c == '>') depth--;
                else if (c == ',' && depth == 0)
                {
                    args.Add(content.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }

            if (start < content.Length)
                args.Add(content.Substring(start).Trim());

            return args.ToArray();
        }

        private static bool VerifyAndAddHash(Dictionary<string, Dictionary<string, string>> fileHashesPerProject,
                                             string fileHash,
                                             Project project,
                                             string relativeFilePath,
                                             string solutionDirectory)
        {
            // Normalize paths to ensure consistent identifiers regardless of how the solution path is passed
            var projectPath = Path.GetFullPath(project.FilePath);

            // Ensure solution directory ends with directory separator for consistent replacement
            var solutionDir = solutionDirectory;
            if (!solutionDir.EndsWith(Path.DirectorySeparatorChar.ToString()))
            {
                solutionDir += Path.DirectorySeparatorChar;
            }

            var identifier = projectPath.StartsWith(solutionDir, StringComparison.OrdinalIgnoreCase)
                                 ? projectPath.Substring(solutionDir.Length)
                                 : projectPath;

            if (!fileHashesPerProject.ContainsKey(identifier))
            {
                fileHashesPerProject.Add(identifier, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            }

            if (fileHashesPerProject[identifier].ContainsKey(relativeFilePath))
            {
                var storedHash = fileHashesPerProject[identifier][relativeFilePath];

                // Exact match - no regeneration needed
                if (storedHash == fileHash)
                {
                    return true;
                }

                // Smart hash comparison: hash format is "source_hash+template_hash"
                // If only the template hash changed (e.g., due to version upgrade),
                // update the stored hash without regenerating
                var storedParts = storedHash.Split('+');
                var newParts = fileHash.Split('+');

                if (storedParts.Length == 2 && newParts.Length == 2)
                {
                    // Compare source file hash part only
                    if (storedParts[0] == newParts[0])
                    {
                        // Source file hasn't changed, only template hash differs
                        // Update to new hash format without regenerating
                        Logger.DebugFormat(
                                           "Source unchanged for {0}, updating template hash from {1} to {2}",
                                           relativeFilePath,
                                           storedParts[1].Substring(0, 8),
                                           newParts[1].Substring(0, 8));
                        fileHashesPerProject[identifier][relativeFilePath] = fileHash;
                        return true; // Skip regeneration
                    }
                }

                // Source or template content actually changed - needs regeneration
                fileHashesPerProject[identifier][relativeFilePath] = fileHash;
            }
            else
            {
                fileHashesPerProject[identifier].Add(relativeFilePath, fileHash);
            }

            return false;
        }

        private async Task<ICollection<string>> GenerateFactoriesInProjectAsync(Compilation compilation,
                                                                                bool isSdkStyleProject,
                                                                                Dictionary<string, Dictionary<string, string>> fileHashesPerProject,
                                                                                Project project)
        {
            var generatedFactoriesList = new List<string>();

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var syntaxRootNode = await syntaxTree.GetRootAsync();
                var classDeclarations =
                    syntaxRootNode.DescendantNodesAndSelf(syntaxNode => syntaxNode is not ClassDeclarationSyntax)
                                  .OfType<ClassDeclarationSyntax>()
                                  .Where(IsTypeDeclarationSyntaxFactoryTarget)
                                  .ToArray();

                if (classDeclarations.Any())
                {
                    foreach (var classDeclarationSyntax in classDeclarations)
                    {
                        var fullyQualifiedMetadataName = GetDeclarationFullName(classDeclarationSyntax);
                        var resolvedConcreteClassType = compilation.GetTypeByMetadataName(fullyQualifiedMetadataName);

                        var result = this.GenerateFactoryForClass(classDeclarationSyntax, resolvedConcreteClassType, compilation, isSdkStyleProject, fileHashesPerProject, project);

                        if (result is null)
                            continue;

                        var (generatedFilePath, generated) = result.Value;
                        generatedFactoriesList.Add(generatedFilePath);

                        if (generated)
                        {
                            Logger.Debug(new string('-', 80));
                        }
                    }
                }
            }

            return generatedFactoriesList;
        }

        private (string, bool)? GenerateFactoryForClass(ClassDeclarationSyntax concreteClassDeclarationSyntax,
                                                        INamedTypeSymbol concreteClassTypeSymbol,
                                                        Compilation compilation,
                                                        bool isSdkStyleProject,
                                                        Dictionary<string, Dictionary<string, string>> fileHashesPerProject,
                                                        Project project)
        {
            INamedTypeSymbol factoryInterfaceTypeSymbol = null;
            string generatedFilePath = null;

            try
            {
                var factoryAttribute = GetFactoryAttributeFromClassDeclaration(concreteClassDeclarationSyntax);

                var genericArgumentTypeSymbols = Array.Empty<INamedTypeSymbol>();

                if (factoryAttribute.ArgumentList != null && factoryAttribute.ArgumentList.Arguments.Any())
                {
                    var typeOfArgument = factoryAttribute.ArgumentList.Arguments.Single();

                    var usings = concreteClassDeclarationSyntax.Parent.DescendantNodes()
                                                               .OfType<UsingDirectiveSyntax>()
                                                               .Select(syntax => syntax.Name.NormalizeWhitespace().ToFullString())
                                                               .ToArray();

                    var namespaces = usings.Concat([GetDeclarationNamespaceFullName(concreteClassDeclarationSyntax)]);

                    var typeOfSyntax = (TypeOfExpressionSyntax)typeOfArgument.Expression;
                    var typeName = typeOfSyntax.Type.ToFullString();

                    if (typeOfSyntax.Type is GenericNameSyntax genericNameSyntax)
                    {
                        var typeArgList = genericNameSyntax.TypeArgumentList.Arguments;
                        var genericArgumentCount = typeArgList.Count;

                        // Determine whether all type arguments are open placeholders, meaning either:
                        //   typeof(IFoo<,,>)          — OmittedTypeArgumentSyntax nodes (commas with no names)
                        //   typeof(IFoo<TState, ...>) — simple identifiers that are the class's own type parameters
                        // Both represent an unbound generic whose parameters are forwarded from the concrete
                        // class; don't try to resolve the placeholder names as concrete types.
                        var classTypeParameterNames = new HashSet<string>(
                                                                          concreteClassTypeSymbol?.TypeParameters.Select(tp => tp.Name) ?? Enumerable.Empty<string>(),
                                                                          StringComparer.Ordinal);

                        bool IsOpenGenericPlaceholder(TypeSyntax arg)
                            => arg is OmittedTypeArgumentSyntax // typeof(IFoo<,,>)
                               || (arg is IdentifierNameSyntax id // typeof(IFoo<TState>)
                                   && classTypeParameterNames.Contains(id.Identifier.ValueText));

                        var isUnboundForwarded = typeArgList.All(IsOpenGenericPlaceholder);

                        if (!isUnboundForwarded)
                        {
                            var genericArgumentNames = typeArgList.Select(a => a.ToFullString().Trim()).ToArray();

                            Logger.DebugFormat("Resolving {0} generic arguments for {1}:", genericArgumentNames.Length, typeName);
                            for (int i = 0; i < genericArgumentNames.Length; i++)
                            {
                                Logger.DebugFormat("  Arg {0}: '{1}'", i, genericArgumentNames[i]);
                            }

                            genericArgumentTypeSymbols = genericArgumentNames
                                                         .Select((arg, idx) =>
                                                                 {
                                                                     var resolved = ResolveTypeByName(arg, namespaces, compilation);
                                                                     if (resolved == null)
                                                                     {
                                                                         Logger.ErrorFormat("Failed to resolve argument {0}: '{1}'", idx, arg);
                                                                     }
                                                                     else
                                                                     {
                                                                         Logger.DebugFormat("Successfully resolved argument {0}: '{1}' -> {2}", idx, arg, resolved);
                                                                     }

                                                                     return resolved;
                                                                 })
                                                         .ToArray();

                            if (genericArgumentTypeSymbols.Any(s => s == null))
                            {
                                var failedTypes = genericArgumentNames
                                                  .Where((name, idx) => genericArgumentTypeSymbols[idx] == null)
                                                  .ToArray();

                                Logger.ErrorFormat("Failed to resolve generic type arguments for {0}", typeName);
                                Logger.ErrorFormat("  Available namespaces: {0}", string.Join(", ", namespaces));
                                Logger.ErrorFormat("  Failed to resolve: {0}", string.Join(", ", failedTypes));

                                throw new InvalidOperationException($"Not all generic types for {typeName} have been found. Failed to resolve: {string.Join(", ", failedTypes)}");
                            }
                        }

                        // Build the metadata name directly from the identifier and arity, e.g. IFoo`3
                        // Avoids any ToFullString() trivia/whitespace issues with the generic suffix.
                        typeName = $"{genericNameSyntax.Identifier.ValueText}`{genericArgumentCount}";
                    }

                    var fullTypeNames = namespaces.Select(ns => $"{ns}.{typeName}").Concat([typeName]);

                    factoryInterfaceTypeSymbol = fullTypeNames.Select(compilation.GetTypeByMetadataName).FirstOrDefault(o => o != null);

                    if (factoryInterfaceTypeSymbol == null)
                    {
                        throw new InvalidOperationException($"Factory {typeName} type couldn't be resolved in {GetDeclarationFullName(concreteClassDeclarationSyntax)}.");
                    }

                    if (factoryInterfaceTypeSymbol.IsUnboundGenericType)
                    {
                        factoryInterfaceTypeSymbol = factoryInterfaceTypeSymbol.ConstructedFrom;
                    }
                }
                else
                {
                    throw new InvalidOperationException($"Factory type must be specified in {factoryAttribute.Name.ToFullString()} on {GetDeclarationFullName(concreteClassDeclarationSyntax)}.");
                }

                var factoryClassGenericName = GetFactoryClassGenericName(concreteClassDeclarationSyntax, factoryInterfaceTypeSymbol);

                // Compute the closed factory interface type symbol to match the logic in RenderFactoryImplementation
                // This determines the actual template name that will be used (for custom per-interface templates)
                var closedFactoryInterfaceTypeSymbol = genericArgumentTypeSymbols.Length > 0
                                                       && genericArgumentTypeSymbols.Length == factoryInterfaceTypeSymbol.TypeParameters.Length
                                                           ? factoryInterfaceTypeSymbol.Construct(genericArgumentTypeSymbols)
                                                           : factoryInterfaceTypeSymbol;
                var factoryInterfaceFullName = closedFactoryInterfaceTypeSymbol.ToString();

                var typeDeclarationDocument = project.GetDocument(concreteClassDeclarationSyntax.SyntaxTree);

                var folders = string.Join(@"\", typeDeclarationDocument.Folders);
                var safeFileName = GetSafeFileName(factoryClassGenericName);
                var fileName = $"{safeFileName}.Generated.cs";
                generatedFilePath = Path.Combine(Path.GetDirectoryName(project.FilePath), folders, fileName);
                var relativeFilePath = Path.Combine(folders, typeDeclarationDocument.Name);

                bool IsLinkedFile()
                    => !typeDeclarationDocument.FilePath.StartsWith(Path.GetDirectoryName(project.FilePath), StringComparison.OrdinalIgnoreCase);

                if (IsLinkedFile())
                    return null;

                using SHA1Managed sha1 = new SHA1Managed();
                var hash = sha1.ComputeHash(File.ReadAllBytes(typeDeclarationDocument.FilePath));
                var fileHashBuilder = new StringBuilder(2 * hash.Length);
                foreach (var b in hash)
                {
                    fileHashBuilder.AppendFormat("{0:X2}", b);
                }

                // Determine which template will actually be used for this factory and compute its hash
                // Pass the original interface symbol, not the closed one, to avoid angle brackets in paths
                var actualTemplateHash = this.GetTemplateHashForFactory(factoryInterfaceTypeSymbol);
                var fileHash = fileHashBuilder.ToString() + "+" + actualTemplateHash;
                if (VerifyAndAddHash(fileHashesPerProject, fileHash, project, relativeFilePath, this.solutionDirectory) && File.Exists(generatedFilePath))
                {
                    return (generatedFilePath, false);
                }

                Logger.DebugFormat("[{0}]", string.Join(", ", concreteClassDeclarationSyntax.AttributeLists.SelectMany(a => a.Attributes).Select(x => x.Name)));
                Logger.Debug(concreteClassDeclarationSyntax.Identifier.Value);

                Logger.Debug($"Rendering: {factoryClassGenericName}\tInterface: {factoryInterfaceFullName}\tTargeting: {GetDeclarationFullName(concreteClassDeclarationSyntax)}");
                this.RenderFactoryImplementation(generatedFilePath, concreteClassDeclarationSyntax, concreteClassTypeSymbol, factoryInterfaceTypeSymbol, GetSafeFileName(factoryClassGenericName), genericArgumentTypeSymbols, isSdkStyleProject);

                return (generatedFilePath, true);
            }
            catch (ArgumentException ex) when (ex.Message.Contains("Illegal characters in path"))
            {
                Logger.ErrorFormat("===== ILLEGAL CHARACTERS IN PATH ERROR =====");
                Logger.ErrorFormat("Concrete class: {0}", GetDeclarationFullName(concreteClassDeclarationSyntax));
                Logger.ErrorFormat("Factory interface: {0}", factoryInterfaceTypeSymbol?.ToString() ?? "(null)");
                Logger.ErrorFormat("  Namespace: {0}", factoryInterfaceTypeSymbol?.ContainingNamespace?.ToString() ?? "(null)");
                Logger.ErrorFormat("  Type name: {0}", factoryInterfaceTypeSymbol?.Name ?? "(null)");
                Logger.ErrorFormat("Template directory: {0}", this.templateDirectory);
                Logger.ErrorFormat("Error details: {0}", ex.Message);
                Logger.ErrorFormat("Stack trace: {0}", ex.StackTrace);
                throw;
            }
        }

        /// <summary>
        /// Resolves which template will be used for a given factory interface and computes its hash.
        /// Mimics the logic in FactoryGeneratorEngine.ResolveTemplatePath to find custom templates.
        /// </summary>
        private string GetTemplateHashForFactory(INamedTypeSymbol factoryInterfaceTypeSymbol)
        {
            var actualTemplatePath = this.GetTemplatePathForFactory(factoryInterfaceTypeSymbol);

            using (var sha1 = new SHA1Managed())
            {
                var templateBytes = File.ReadAllBytes(actualTemplatePath);
                var hashBytes = sha1.ComputeHash(templateBytes);
                return BitConverter.ToString(hashBytes).Replace("-", string.Empty);
            }
        }

        private string GetTemplatePathForFactory(INamedTypeSymbol factoryInterfaceTypeSymbol)
        {
            // Build the fully qualified name without generic type parameters for template file lookup
            // Template files are named like: Liebherr.Lioba.Contracts.Services.IFeatureServiceFactory.render
            // NOT: IFeatureServiceFactory<T>.render (no angle brackets)
            var containingNamespace = factoryInterfaceTypeSymbol.ContainingNamespace?.ToString();
            var typeName = factoryInterfaceTypeSymbol.Name; // Simple name without generic parameters
            var templateName = string.IsNullOrEmpty(containingNamespace)
                                   ? typeName
                                   : $"{containingNamespace}.{typeName}";

            var customTemplatePath = Path.Combine(this.templateDirectory, $"{templateName}.render");
            return File.Exists(customTemplatePath)
                       ? customTemplatePath
                       : this.templatePath;
        }

        private ImmutableHashSet<string> GetTemplateUsingNamespaces(INamedTypeSymbol factoryInterfaceTypeSymbol)
        {
            var templatePath = this.GetTemplatePathForFactory(factoryInterfaceTypeSymbol);
            return this.templateUsingNamespacesByPath.GetOrAdd(
                                                               templatePath,
                                                               path =>
                                                               {
                                                                   var templateText = File.ReadAllText(path);
                                                                   var parseableTemplate = System.Text.RegularExpressions.Regex.Replace(
                                                                                                                                        templateText,
                                                                                                                                        @"\{\{.*?\}\}",
                                                                                                                                        string.Empty,
                                                                                                                                        System.Text.RegularExpressions.RegexOptions.Singleline);
                                                                   var root = CSharpSyntaxTree.ParseText(parseableTemplate).GetCompilationUnitRoot();
                                                                   var templateNamespaces = root.DescendantNodes()
                                                                                                .OfType<UsingDirectiveSyntax>()
                                                                                                .Where(usingDirective => usingDirective.Alias == null
                                                                                                                         && !usingDirective.StaticKeyword.IsKind(SyntaxKind.StaticKeyword))
                                                                                                .Select(usingDirective => usingDirective.Name.ToString().Trim())
                                                                                                .Select(namespaceName => namespaceName.StartsWith("global::", StringComparison.Ordinal)
                                                                                                                             ? namespaceName.Substring("global::".Length)
                                                                                                                             : namespaceName);

                                                                   return templateNamespaces.ToImmutableHashSet(StringComparer.Ordinal);
                                                               });
        }

        private void RemoveObsoleteFactoriesFromSolution(string[] obsoleteFactoryFilePaths,
                                                         Dictionary<string, bool> isSdkStyleProjectDict)
        {
            if (obsoleteFactoryFilePaths.Length == 0)
            {
                return;
            }

            var newSolution = this.solution;

            Logger.InfoFormat("Removing {0} from solution...", "obsolete generated factory file".ToQuantity(obsoleteFactoryFilePaths.Length));

            foreach (var obsoleteFactoryFilePath in obsoleteFactoryFilePaths)
            {
                var obsoleteFactoryDocument = FindDocumentFromPath(newSolution, obsoleteFactoryFilePath);
                if (!isSdkStyleProjectDict[obsoleteFactoryDocument.Project.FilePath])
                {
                    var newProject = obsoleteFactoryDocument.Project.RemoveDocument(obsoleteFactoryDocument.Id);
                    newSolution = newProject.Solution;
                }

                Logger.InfoFormat("{0} from {1}.", Path.GetFileName(obsoleteFactoryFilePath), obsoleteFactoryDocument.Project.Name);
                File.Delete(obsoleteFactoryFilePath);
            }

            Interlocked.Exchange(ref this.solution, newSolution);
        }

        private void RemoveObsoleteHashes(Dictionary<string, Dictionary<string, string>> fileHashesPerProject)
        {
            foreach (var projectPath in fileHashesPerProject.Keys.ToArray())
            {
                var project = this.solution.Projects.FirstOrDefault(p => Path.GetFullPath(p.FilePath).Equals(Path.GetFullPath(Path.Combine(this.solutionDirectory, projectPath)), StringComparison.OrdinalIgnoreCase));

                if (project == null)
                {
                    fileHashesPerProject.Remove(projectPath);
                    continue;
                }

                foreach (var file in fileHashesPerProject[projectPath].Keys.ToArray())
                {
                    var filePath = Path.Combine(Path.GetDirectoryName(project.FilePath), file);
                    if (!File.Exists(filePath))
                    {
                        fileHashesPerProject[projectPath].Remove(file);
                    }
                }
            }
        }

        private void RenderFactoryImplementation(string generatedFilePath,
                                                 ClassDeclarationSyntax concreteClassDeclarationSyntax,
                                                 INamedTypeSymbol concreteClassTypeSymbol,
                                                 INamedTypeSymbol factoryInterfaceTypeSymbol,
                                                 string factoryName,
                                                 INamedTypeSymbol[] genericArgumentTypeSymbols,
                                                 bool isSdkStyleProject)
        {
            var fileName = Path.GetFileName(generatedFilePath);
            var factoryInterfaceName = factoryInterfaceTypeSymbol.Name;

            // When concrete type arguments are provided (e.g. typeof(IFoo<ConcreteA, ConcreteB>)),
            // construct the closed generic interface so that all method return types and parameter
            // types already carry the concrete types. This ensures the generated method signatures
            // use the real types rather than the open type-parameter names.
            // For non-generic factories and open-generic-forwarding factories (typeof(IFoo<,,>)),
            // the symbol is used as-is — the factory class itself stays generic.
            var closedFactoryInterfaceTypeSymbol = genericArgumentTypeSymbols.Length > 0
                                                   && genericArgumentTypeSymbols.Length == factoryInterfaceTypeSymbol.TypeParameters.Length
                                                       ? factoryInterfaceTypeSymbol.Construct(genericArgumentTypeSymbols)
                                                       : factoryInterfaceTypeSymbol;

            var (project, typeDeclarationDocument) = this.solution.Projects.Select(proj => (proj, doc: proj.GetDocument(concreteClassDeclarationSyntax.SyntaxTree))).First(t => t.doc != null);
            var factoryGeneratorEngine = new FactoryGeneratorEngine(project, this.templatePath);
            var templateUsingNamespaces = this.GetTemplateUsingNamespaces(factoryInterfaceTypeSymbol);

            // Get the semantic model to ensure we have the most accurate symbol information including constraints
            var semanticModel = typeDeclarationDocument.GetSemanticModelAsync().Result;
            var concreteClassSymbolFromSemantics = semanticModel.GetDeclaredSymbol(concreteClassDeclarationSyntax) as INamedTypeSymbol;
            // Use the symbol from semantics if available, as it has better constraint information from source
            var concreteTypeSymbolForConstraints = concreteClassSymbolFromSemantics ?? concreteClassTypeSymbol;

            var usingsToFilterOut = new[] { concreteClassTypeSymbol.ContainingNamespace.ToString() };

            // Collect extern alias directives from both the concrete class and the factory interface.
            // Externs can appear at file scope (CompilationUnitSyntax.Externs) or inside namespace
            // blocks (BaseNamespaceDeclarationSyntax.Externs). Collect from both locations.
            var concreteCompilation = concreteClassDeclarationSyntax.FirstAncestorOrSelf<CompilationUnitSyntax>();
            var concreteNamespace = concreteClassDeclarationSyntax.FirstAncestorOrSelf<BaseNamespaceDeclarationSyntax>();
            var concreteExterns = (concreteCompilation?.Externs ?? default)
                .Concat(concreteNamespace?.Externs ?? default);

            var interfaceExterns = factoryInterfaceTypeSymbol.DeclaringSyntaxReferences
                                                             .SelectMany(r =>
                                                                         {
                                                                             var syntax = r.GetSyntax();
                                                                             var fileExterns = syntax.FirstAncestorOrSelf<CompilationUnitSyntax>()?.Externs ?? default;
                                                                             var nsExterns = syntax.FirstAncestorOrSelf<BaseNamespaceDeclarationSyntax>()?.Externs ?? default;
                                                                             return fileExterns.Concat(nsExterns);
                                                                         });

            var allExterns = concreteExterns.Concat(interfaceExterns)
                                            .GroupBy(e => e.Identifier.Text)
                                            .Select(g => g.First())
                                            .ToArray();

            var outerUsingDeclarations = FilterOutUsings(concreteClassDeclarationSyntax.FirstAncestorOrSelf<CompilationUnitSyntax>().Usings, usingsToFilterOut);
            var innerUsingDeclarations = FilterOutUsings(concreteClassDeclarationSyntax.FirstAncestorOrSelf<BaseNamespaceDeclarationSyntax>().Usings, usingsToFilterOut);
            outerUsingDeclarations = FilterTemplateUsings(outerUsingDeclarations, templateUsingNamespaces);
            innerUsingDeclarations = FilterTemplateUsings(innerUsingDeclarations, templateUsingNamespaces);

            // Collect all usings from the concrete class (including alias-qualified ones from the class itself)
            var concreteUsingsAll = (concreteCompilation?.Usings ?? default)
                                    .Concat(concreteNamespace?.Usings ?? default)
                                    .ToArray();

            // Also pull in usings from the factory interface's source file — it may reference
            // types (e.g. parameter types in Create()) whose namespaces are not imported by the
            // concrete class's file. Collect both file-level and namespace-level usings, including
            // alias-qualified usings (e.g. ExternAlias::Namespace) which reference extern aliases.
            var interfaceUsings = factoryInterfaceTypeSymbol.DeclaringSyntaxReferences
                                                            .Select(r => r.GetSyntax())
                                                            .Where(n => n != null)
                                                            .SelectMany(n =>
                                                                        {
                                                                            var compilationUsings = n.FirstAncestorOrSelf<CompilationUnitSyntax>()?.Usings ?? default;
                                                                            var namespaceUsings = n.FirstAncestorOrSelf<BaseNamespaceDeclarationSyntax>()?.Usings ?? default;
                                                                            return compilationUsings.Concat(namespaceUsings);
                                                                        })
                                                            .ToArray();

            // Combine usings from both sources
            var allUsings = concreteUsingsAll.Concat(interfaceUsings).ToArray();

            // For alias-qualified usings (e.g. ExternAlias::Namespace.Type), extract the
            // unqualified namespace names. The Name property for an alias-qualified using
            // is an AliasQualifiedNameSyntax, so we need to extract the right side (after ::).
            var aliasQualifiedNamespaces = allUsings
                                           .Where(u => u.Alias != null)
                                           .Select(u =>
                                                   {
                                                       // For "using Alias::Namespace;", u.Name might be an AliasQualifiedNameSyntax
                                                       // where ToString() includes "Alias::Namespace". We want just "Namespace".
                                                       var name = u.Name;
                                                       if (name is Microsoft.CodeAnalysis.CSharp.Syntax.AliasQualifiedNameSyntax aliasQual)
                                                       {
                                                           // Extract just the right side (after ::)
                                                           name = aliasQual.Name;
                                                       }

                                                       var nameText = name.ToString().Trim();
                                                       Logger.DebugFormat("Alias-qualified using: Alias={0}, Extracted namespace={1}", u.Alias.Name.Identifier.Text, nameText);
                                                       return nameText;
                                                   })
                                           .Where(ns => !string.IsNullOrWhiteSpace(ns))
                                           .ToList();

            // Collect all namespaces visible in the generated file so we can shorten FQNs.
            // Include both unaliased usings AND alias-qualified usings, because once a using
            // directive (even alias-qualified) brings a namespace into scope, types from that
            // namespace can be referenced without the full prefix.
            var @namespace = GetDeclarationNamespaceFullName(concreteClassDeclarationSyntax);
            var knownNamespaces = allUsings
                                  .Where(u => u.Alias == null)
                                  .Select(u => u.Name.ToString())
                                  .Concat(aliasQualifiedNamespaces) // Also include alias-qualified namespaces
                                  .Concat(templateUsingNamespaces)
                                  .Append(@namespace)
                                  .Append(concreteClassTypeSymbol.ContainingNamespace.ToString())
                                  .Distinct()
                                  .ToList();

            // Merge interface usings into innerUsingDeclarations, skipping any already present
            // or belonging to the generated file's own namespace. Track both simple names for
            // unaliased usings and full text for alias-qualified usings to avoid duplicates.
            var existingUsingSignatures = new HashSet<string>(
                                                              outerUsingDeclarations.Concat(innerUsingDeclarations)
                                                                                    .Select(u => u.ToFullString().Trim()));
            existingUsingSignatures.Add(@namespace);
            existingUsingSignatures.UnionWith(usingsToFilterOut);

            // Take the leading trivia (indentation) from an existing inner using so the
            // appended directives match the file's indentation style exactly.
            var innerUsingLeadingTrivia = innerUsingDeclarations.Count > 0
                                              ? innerUsingDeclarations[0].GetLeadingTrivia()
                                              : SyntaxFactory.TriviaList(SyntaxFactory.Whitespace("    "));

            foreach (var u in allUsings)
            {
                if (IsUsingProvidedByTemplate(u, templateUsingNamespaces))
                {
                    continue;
                }

                var signature = u.ToFullString().Trim();
                if (existingUsingSignatures.Add(signature))
                    innerUsingDeclarations = innerUsingDeclarations.Add(
                                                                        u.WithLeadingTrivia(innerUsingLeadingTrivia)
                                                                         .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed));
            }

            var factoryInterfaceMethods = GetSuitableFactoryInterfaceMethods(concreteClassTypeSymbol, closedFactoryInterfaceTypeSymbol, genericArgumentTypeSymbols);

            var allContractMethodParameters = factoryInterfaceMethods.SelectMany(contractTypeMethod => contractTypeMethod.symbol.Parameters).ToArray();
            if (!factoryInterfaceMethods.Any())
            {
                throw new InvalidOperationException($"The interface {closedFactoryInterfaceTypeSymbol} has no suitable method returning any interface implemented by {concreteClassTypeSymbol}. Please check if their is any.");
            }

            // Ensure every namespace referenced by factory method parameter types and return types
            // has a corresponding using directive. This catches implicit namespaces (e.g. 'System'
            // for IObservable<T>) that are never explicitly imported in the source files.
            // Collect all missing namespaces first, sort them (System.* first, then alphabetically),
            // then insert them at the correct position in the using list.
            // Skip namespaces already covered by alias-qualified usings to avoid duplicates.
            var allFactoryTypes = factoryInterfaceMethods
                .SelectMany(m => m.symbol.Parameters.Select(p => p.Type).Append(m.returnType));
            // Track namespace strings (not full using signatures) to detect missing namespaces
            var existingNamespaceNames = new HashSet<string>(knownNamespaces);
            existingNamespaceNames.UnionWith(aliasQualifiedNamespaces); // Prevent duplicate plain usings for alias-qualified namespaces
            var missingNamespaces = CollectNamespacesFromTypes(allFactoryTypes)
                                    .Select(ns => ns.ToString())
                                    .Where(nsName => !string.IsNullOrEmpty(nsName) && existingNamespaceNames.Add(nsName))
                                    .OrderBy(nsName => nsName.StartsWith("System", StringComparison.Ordinal)
                                                           ? 0
                                                           : 1)
                                    .ThenBy(nsName => nsName, StringComparer.Ordinal)
                                    .ToList();

            foreach (var nsName in missingNamespaces)
            {
                knownNamespaces.Add(nsName);
                // " " prefix on the name gives the required space between 'using' keyword and name.
                var newUsing = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(" " + nsName))
                                            .WithLeadingTrivia(innerUsingLeadingTrivia)
                                            .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);

                // Insert System.* usings before the first non-System using; append others at end.
                if (nsName.StartsWith("System", StringComparison.Ordinal))
                {
                    var insertIdx = innerUsingDeclarations
                                    .Select((u, i) => (u, i))
                                    .FirstOrDefault(t => !t.u.Name.ToString().TrimStart().StartsWith("System", StringComparison.Ordinal))
                                    .i;
                    innerUsingDeclarations = innerUsingDeclarations.Insert(insertIdx, newUsing);
                }
                else
                {
                    innerUsingDeclarations = innerUsingDeclarations.Add(newUsing);
                }
            }

            var allConstructorParameters = factoryInterfaceMethods.Select(factoryMethod => SelectConstructorFromFactoryMethod(factoryMethod.symbol, concreteClassTypeSymbol))
                                                                  .SelectMany(selectedConstructor => selectedConstructor.Parameters).ToArray();
            var constructorParametersUsingSelfType = allConstructorParameters.Where(p => p.Type.Name == factoryInterfaceTypeSymbol.Name).ToArray();
            var constructorParametersWithoutSelfType = allConstructorParameters.Except(constructorParametersUsingSelfType)
                                                                               .ToArray();
#pragma warning disable RS1024 // Symbols should be compared for equality
            var injectedParameters = (from parameter in (IEnumerable<IParameterSymbol>)constructorParametersWithoutSelfType
                                      where !allContractMethodParameters.Any(contractMethodParameter => CompareParameters(contractMethodParameter, parameter))
                                      select parameter).Distinct(new ParameterEqualityComparer()).ToArray();
#pragma warning restore RS1024 // Symbols should be compared for equality

            var concreteClassName = GetXmlDocSafeTypeName(concreteClassTypeSymbol.ToString());
            var generateCodeArguments = new[] { new Value("\"DeveloperInTheFlow.FactoryGenerator\"", false), new Value($"\"{this.version}\"", true) };

            // Class attributes
            var classAttributes = concreteClassDeclarationSyntax.AttributeLists
                                                                .SelectMany(al => al.Attributes).Where(attributeSyntax =>
                                                                                                       {
                                                                                                           var attributeName = attributeSyntax.Name.ToString();
                                                                                                           return this.attributeImportList.Any(attributeName.Contains);
                                                                                                       })
                                                                .Select(a =>
                                                                        {
                                                                            var arguments = Enumerable.Empty<Value>();
                                                                            if (a.ArgumentList != null && a.ArgumentList.Arguments.Any())
                                                                            {
                                                                                var lastIndex = a.ArgumentList.Arguments.Count - 1;
                                                                                arguments = a.ArgumentList.Arguments.Select((arg,
                                                                                                                             i) => new Value(arg.ToString(), i == lastIndex));
                                                                            }

                                                                            return Attribute.Create(a.Name.ToString(), arguments);
                                                                        })
                                                                .Concat(
                                                                [
                                                                    Attribute.Create("global::System.CodeDom.Compiler.GeneratedCode", generateCodeArguments),
                                                                    Attribute.Create("global::System.Diagnostics.DebuggerNonUserCodeAttribute")
                                                                ]).ToArray();

            // Generic types of the factory — merge concrete class constraints by position for open-generic forwarding.
            // Materialize to array so both GenericTypes and GenericTypeConstraints can iterate it independently.
            // Use the semantic model-based symbol to ensure constraint information is available from source
            var concreteTypeParams = concreteTypeSymbolForConstraints?.TypeParameters ?? default;
            if (concreteTypeParams.IsDefaultOrEmpty && concreteClassDeclarationSyntax.TypeParameterList != null)
            {
                // This shouldn't happen, but log it if it does
                Logger.Warn($"Type parameters not available for {concreteClassDeclarationSyntax.Identifier.ValueText}, constraints may be missing");
                concreteTypeParams = default;
            }

            var classGenericTypes = this.genericTypeBuilderService.Build(factoryInterfaceTypeSymbol.TypeParameters, genericArgumentTypeSymbols, concreteTypeParams).ToArray();

            // Constructor of the factory
            var constructor = this.constructorBuilderService.Build(concreteClassDeclarationSyntax, injectedParameters, knownNamespaces);

            // Fields of the factory
            var fields = this.fieldsBuilderService.Build(injectedParameters, knownNamespaces).ToArray();

            // Methods of the factory
            var methods = this.methodsBuilderService.Build(concreteClassTypeSymbol, fields, injectedParameters, factoryInterfaceMethods, factoryInterfaceName, factoryInterfaceTypeSymbol, knownNamespaces);

            // Interface of the factory — use the closed symbol so it already has concrete type arguments
            var inheritFqn = closedFactoryInterfaceTypeSymbol.ToString();
            var inherit = ShortenTypeName(inheritFqn, knownNamespaces);

            // The factory
            var factoryClass = new Class(classAttributes, concreteClassName, constructor, methods, fields, classGenericTypes, inherit, factoryName);

            // The file containing the factory
            // Place extern aliases inside the namespace (before inner usings) to match C# convention
            // where externs from interface namespace blocks are preserved
            var innerContent = allExterns.Any()
                                   ? string.Concat(allExterns.Select(e => "    " + e.ToFullString())) + "\r\n" + innerUsingDeclarations.ToFullString()
                                   : innerUsingDeclarations.ToFullString();
            var factoryFile = FactoryFile.Create(
                                                 @namespace,
                                                 factoryClass,
                                                 innerContent,
                                                 outerUsingDeclarations.ToFullString());

            object model = factoryFile;
            // Build template name without generic type parameters to avoid illegal characters in path
            var containingNamespace = factoryInterfaceTypeSymbol.ContainingNamespace?.ToString();
            var simpleTypeName = factoryInterfaceTypeSymbol.Name;
            var templateName = string.IsNullOrEmpty(containingNamespace)
                                   ? simpleTypeName
                                   : $"{containingNamespace}.{simpleTypeName}";

            var transformationScript = Path.Combine(this.templateDirectory, $"{templateName}.tcs");

            // Execute the script associated to the template in order to adapt the model for the template whether it exists.
            if (File.Exists(transformationScript))
            {
                var json = JObject.FromObject(factoryFile);
                var attributesArray = (JArray)json["Class"]["Attributes"];
                var factoryForAttributes = attributesArray.Where(a => a["Name"]?.ToString().Contains("FactoryFor") == true).ToList();

                model = factoryForAttributes.Count > 1
                            ? this.TransformMultipleFactoryForAttributes(json, factoryForAttributes, transformationScript)
                            : this.Transform(json, transformationScript);
            }

            var projectFolder = Path.GetDirectoryName(project.FilePath);
            if (projectFolder == null)
            {
                throw new InvalidOperationException("Cannot determines the folder path of the project.");
            }

            // The result of the generator
            // Pass the unbound generic type name for template resolution (matches .render file naming)
            var factoryResult = factoryGeneratorEngine.Generate(model, templateName);

            if (!isSdkStyleProject)
            {
                if (!project.Documents.Any(doc => doc.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase) && doc.Folders.SequenceEqual(typeDeclarationDocument.Folders)))
                {
                    var document = project.AddDocument(fileName, factoryResult.Code, typeDeclarationDocument.Folders);
                    this.solution = document.Project.Solution;
                }
            }

            File.WriteAllText(generatedFilePath, factoryResult.Code, Encoding.UTF8);
        }

        private JObject Transform(JObject factoryFile,
                                  string transformationScriptPath)
        {
            dynamic script = this.csscriptEvaluator.Value.LoadFile(transformationScriptPath);
            return script.Transform(factoryFile);
        }

        private JObject TransformMultipleFactoryForAttributes(JObject json,
                                                              List<JToken> factoryForAttributes,
                                                              string transformationScript)
        {
            var attributesArray = (JArray)json["Class"]["Attributes"];
            var systemAttributeNames = new[] { "global::System.CodeDom.Compiler.GeneratedCode", "global::System.Diagnostics.DebuggerNonUserCodeAttribute" };

            // Separate FactoryFor from other attributes
            var otherAttributes = attributesArray.Where(a => a["Name"]?.ToString().Contains("FactoryFor") != true).ToList();
            attributesArray.Clear();
            foreach (var attr in otherAttributes) attributesArray.Add(attr);

            // Transform each FactoryFor and collect newly generated attributes
            var transformedAttributes = new List<JToken>();
            foreach (var factoryForAttr in factoryForAttributes)
            {
                var tempJson = (JObject)json.DeepClone();
                ((JArray)tempJson["Class"]["Attributes"]).Add(factoryForAttr);

                if (this.Transform(tempJson, transformationScript) is JObject transformedObj)
                {
                    var newAttributes = ((JArray)transformedObj["Class"]["Attributes"])
                        .Where(attr =>
                               {
                                   var name = attr["Name"]?.ToString();
                                   return !systemAttributeNames.Contains(name) &&
                                          otherAttributes.All(o => o["Name"]?.ToString() != name);
                               });
                    transformedAttributes.AddRange(newAttributes);
                }
            }

            // Transform base model and add all transformed attributes
            var model = this.Transform(json, transformationScript);
            if (model != null)
            {
                var finalAttrs = (JArray)model["Class"]["Attributes"];
                foreach (var attr in transformedAttributes) finalAttrs.Add(attr);
            }

            return model;
        }

        #endregion

        private class ParameterEqualityComparer : IEqualityComparer<IParameterSymbol>
        {
            #region Public Methods and Operators

            /// <summary>
            /// Determines whether the specified objects are equal.
            /// </summary>
            /// <returns>
            /// true if the specified objects are equal; otherwise, false.
            /// </returns>
            /// <param name="x">The first object of type <paramref name="T"/> to compare.</param><param name="y">The second object of type <paramref name="T"/> to compare.</param>
            public bool Equals(IParameterSymbol x,
                               IParameterSymbol y)
            {
                return x.Name.Equals(y.Name) && x.Type.Name.Equals(y.Type.Name);
            }

            /// <summary>
            /// Returns a hash code for the specified object.
            /// </summary>
            /// <returns>
            /// A hash code for the specified object.
            /// </returns>
            /// <param name="obj">The <see cref="T:System.Object"/> for which a hash code is to be returned.</param><exception cref="T:System.ArgumentNullException">The type of <paramref name="obj"/> is a reference type and <paramref name="obj"/> is null.</exception>
            public int GetHashCode(IParameterSymbol obj)
            {
                unchecked
                {
                    return ((obj.Name != null
                                 ? obj.Name.GetHashCode()
                                 : 0) * 397) ^ (obj.Type.Name != null
                                                    ? obj.Type.Name.GetHashCode()
                                                    : 0);
                }
            }

            #endregion
        }
    }
}