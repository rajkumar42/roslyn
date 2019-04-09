﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Composition;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;

namespace Microsoft.CodeAnalysis.Completion.Providers
{
    internal interface ITypeImportCompletionService : IWorkspaceService
    {
        ImmutableArray<CompletionItem> GetAccessibleTopLevelTypesFromPEReference(
            Solution solution,
            Compilation compilation,
            PortableExecutableReference peReference,
            CancellationToken cancellationToken);

        Task<ImmutableArray<CompletionItem>> GetAccessibleTopLevelTypesFromCompilationReferenceAsync(
            Solution solution,
            Compilation compilation,
            CompilationReference compilationReference,
            CancellationToken cancellationToken);

        /// <summary>
        /// Get all the top level types from given project. This method is intended to be used for 
        /// getting types from source only, so the project must support compilation. 
        /// For getting types from PE, use <see cref="GetAccessibleTopLevelTypesFromPEReference"/>.
        /// </summary>
        Task<ImmutableArray<CompletionItem>> GetAccessibleTopLevelTypesFromProjectAsync(
            Project project,
            CancellationToken cancellationToken);
    }

    [ExportWorkspaceServiceFactory(typeof(ITypeImportCompletionService), ServiceLayer.Editor), Shared]
    internal sealed class TypeImportCompletionService : IWorkspaceServiceFactory
    {
        private readonly ConcurrentDictionary<string, ReferenceCacheEntry> _peItemsCache
            = new ConcurrentDictionary<string, ReferenceCacheEntry>();

        private readonly ConcurrentDictionary<ProjectId, ReferenceCacheEntry> _projectItemsCache
            = new ConcurrentDictionary<ProjectId, ReferenceCacheEntry>();

        public IWorkspaceService CreateService(HostWorkspaceServices workspaceServices)
        {
            var cacheService = workspaceServices.GetService<IWorkspaceCacheService>();
            if (cacheService != null)
            {
                cacheService.CacheFlushRequested += OnCacheFlushRequested;
            }

            return new Service(_peItemsCache, _projectItemsCache);
        }

        private void OnCacheFlushRequested(object sender, EventArgs e)
        {
            _peItemsCache.Clear();
            _projectItemsCache.Clear();
        }

        private class Service : ITypeImportCompletionService
        {
            private readonly ConcurrentDictionary<string, ReferenceCacheEntry> _peItemsCache;
            private readonly ConcurrentDictionary<ProjectId, ReferenceCacheEntry> _projectItemsCache;

            public Service(ConcurrentDictionary<string, ReferenceCacheEntry> peReferenceCache, ConcurrentDictionary<ProjectId, ReferenceCacheEntry> projectReferenceCache)
            {
                _peItemsCache = peReferenceCache;
                _projectItemsCache = projectReferenceCache;
            }

            public async Task<ImmutableArray<CompletionItem>> GetAccessibleTopLevelTypesFromProjectAsync(
                Project project,
                CancellationToken cancellationToken)
            {
                if (!project.SupportsCompilation)
                {
                    throw new ArgumentException(nameof(project));
                }

                var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                var checksum = await SymbolTreeInfo.GetSourceSymbolsChecksumAsync(project, cancellationToken).ConfigureAwait(false);

                return GetAccessibleTopLevelTypesWorker(project.Id, compilation.Assembly.GlobalNamespace, checksum, isInternalsVisible: true, _projectItemsCache, cancellationToken);
            }

            public async Task<ImmutableArray<CompletionItem>> GetAccessibleTopLevelTypesFromCompilationReferenceAsync(
                Solution solution,
                Compilation compilation,
                CompilationReference compilationReference,
                CancellationToken cancellationToken)
            {
                if (!(compilation.GetAssemblyOrModuleSymbol(compilationReference) is IAssemblySymbol assemblySymbol))
                {
                    return ImmutableArray<CompletionItem>.Empty;
                }

                var isInternalsVisible = compilation.Assembly.IsSameAssemblyOrHasFriendAccessTo(assemblySymbol);
                var assemblyProject = solution.GetProject(assemblySymbol, cancellationToken);
                var checksum = await SymbolTreeInfo.GetSourceSymbolsChecksumAsync(assemblyProject, cancellationToken).ConfigureAwait(false);

                return GetAccessibleTopLevelTypesWorker(assemblyProject.Id, assemblySymbol.GlobalNamespace, checksum, isInternalsVisible, _projectItemsCache, cancellationToken);
            }

            public ImmutableArray<CompletionItem> GetAccessibleTopLevelTypesFromPEReference(
                Solution solution,
                Compilation compilation,
                PortableExecutableReference peReference,
                CancellationToken cancellationToken)
            {
                if (!(compilation.GetAssemblyOrModuleSymbol(peReference) is IAssemblySymbol assemblySymbol))
                {
                    return ImmutableArray<CompletionItem>.Empty;
                }

                var key = GetReferenceKey(peReference);
                var isInternalsVisible = compilation.Assembly.IsSameAssemblyOrHasFriendAccessTo(assemblySymbol);
                var rootNamespaceSymbol = assemblySymbol.GlobalNamespace;

                if (key == null)
                {
                    // Can't cache items for reference with null key, so just create them and return. 
                    return GetCompletionItemsForTopLevelTypeDeclarations(rootNamespaceSymbol, isInternalsVisible);
                }

                var checksum = SymbolTreeInfo.GetMetadataChecksum(solution, peReference, cancellationToken);
                return GetAccessibleTopLevelTypesWorker(key, rootNamespaceSymbol, checksum, isInternalsVisible, _peItemsCache, cancellationToken);

                static string GetReferenceKey(PortableExecutableReference reference)
                    => reference.FilePath ?? reference.Display;
            }

            private static ImmutableArray<CompletionItem> GetAccessibleTopLevelTypesWorker<TKey>(
                TKey key,
                INamespaceSymbol rootNamespace,
                Checksum checksum,
                bool isInternalsVisible,
                ConcurrentDictionary<TKey, ReferenceCacheEntry> cache,
                CancellationToken cancellationToken)
            {
                var tick = Environment.TickCount;
                var created = ImmutableArray<CompletionItem>.Empty;
#if DEBUG
                try
#endif
                {
                    // Cache miss, create all requested items.
                    if (!cache.TryGetValue(key, out var cacheEntry) ||
                        cacheEntry.Checksum != checksum ||
                        !AccessibilityMatch(cacheEntry.IncludeInternalTypes, isInternalsVisible))
                    {
                        var items = GetCompletionItemsForTopLevelTypeDeclarations(rootNamespace, isInternalsVisible);
                        cache[key] = new ReferenceCacheEntry(checksum, isInternalsVisible, items);

                        created = items;
                        return items;
                    }

                    return cacheEntry.CachedItems;
                }
#if DEBUG
                finally
                {
                    tick = Environment.TickCount - tick;

                    if (key is string)
                    {
                        DebugObject.debug_total_pe++;
                        DebugObject.debug_total_pe_decl_created += created.Length;
                        DebugObject.debug_total_pe_time += tick;
                    }
                    else
                    {
                        if (DebugObject.IsCurrentCompilation)
                        {
                            DebugObject.debug_total_compilation_decl_created += created.Length;
                            DebugObject.debug_total_compilation_time += tick;
                        }
                        else
                        {
                            DebugObject.debug_total_compilationRef++;
                            DebugObject.debug_total_compilationRef_decl_created += created.Length;
                            DebugObject.debug_total_compilationRef_time += tick;
                        }
                    }
                }
#endif
                static bool AccessibilityMatch(bool includeInternalTypes, bool isInternalsVisible)
                {
                    // If the acceesibility of cached items is differenct from minimum accessibility that is visible from the 
                    // requesting project (for example, if we only have public types for a reference in the cache, but current requesting
                    // project has IVT to the reference), we simply drop the cache entry and recalculate everything for this reference from symbols.
                    // This shouldn't almost never affect PE references, and only affect source references for the first invocation after jumping between
                    // projects with difference access levels, which is much rarer than typing in same document. So basically, this is trading performance
                    // in rarer situation for simplicity> Otherwise, we need to keep track the accessibility of indivual types and maintain two items 
                    // for each internal types (one for proejct with IVT, another one for project without).

                    // TODO: add telemetry to validate this assumption.
                    return isInternalsVisible == includeInternalTypes;
                }
            }

            private static ImmutableArray<CompletionItem> GetCompletionItemsForTopLevelTypeDeclarations(
                INamespaceSymbol rootNamespaceSymbol,
                bool isInternalsVisible)
            {
                var builder = ArrayBuilder<CompletionItem>.GetInstance();
                VisitNamespace(rootNamespaceSymbol, null, isInternalsVisible, builder);
                return builder.ToImmutableAndFree();

                static void VisitNamespace(
                    INamespaceSymbol symbol,
                    string containingNamespace,
                    bool isInternalsVisible,
                    ArrayBuilder<CompletionItem> builder)
                {
                    containingNamespace = ConcatNamespace(containingNamespace, symbol.Name);

                    foreach (var memberNamespace in symbol.GetNamespaceMembers())
                    {
                        VisitNamespace(memberNamespace, containingNamespace, isInternalsVisible, builder);
                    }

                    var overloads = PooledDictionary<string, TypeOverloadInfo>.GetInstance();
                    var memberTypes = symbol.GetTypeMembers();

                    foreach (var memberType in memberTypes)
                    {
                        if (IsAccessible(memberType.DeclaredAccessibility, isInternalsVisible)
                            && memberType.CanBeReferencedByName)
                        {
                            if (!overloads.TryGetValue(memberType.Name, out var overloadInfo))
                            {
                                overloadInfo = default;
                            }
                            overloads[memberType.Name] = overloadInfo.Aggregate(memberType);
                        }
                    }

                    foreach (var pair in overloads)
                    {
                        var overloadInfo = pair.Value;
                        if (overloadInfo.NonGenericOverload != null)
                        {
                            var item = TypeImportCompletionItem.Create(overloadInfo.NonGenericOverload, containingNamespace, overloadInfo.Count - 1);
                            builder.Add(item);
                        }

                        if (overloadInfo.BestGenericOverload != null)
                        {
                            var item = TypeImportCompletionItem.Create(overloadInfo.BestGenericOverload, containingNamespace, overloadInfo.Count - 1);
                            builder.Add(item);
                        }
                    }

                }

                static bool IsAccessible(Accessibility declaredAccessibility, bool isInternalsVisible)
                {
                    // For top level types, default accessibility is `internal`
                    return isInternalsVisible
                        ? declaredAccessibility >= Accessibility.Internal || declaredAccessibility == Accessibility.NotApplicable
                        : declaredAccessibility >= Accessibility.Public;
                }
            }
        }

        private static string ConcatNamespace(string containingNamespace, string name)
        {
            Debug.Assert(name != null);
            if (string.IsNullOrEmpty(containingNamespace))
            {
                return name;
            }

            var @namespace = containingNamespace + "." + name;
#if DEBUG
            DebugObject.debug_total_namespace_concat++;
            DebugObject.Namespaces.Add(@namespace);
#endif
            return @namespace;
        }

        private readonly struct TypeOverloadInfo
        {
            public TypeOverloadInfo(INamedTypeSymbol nonGenericOverload, INamedTypeSymbol bestGenericOverload, int count)
            {
                NonGenericOverload = nonGenericOverload;
                BestGenericOverload = bestGenericOverload;
                Count = count;
            }

            public INamedTypeSymbol NonGenericOverload { get; }

            // Generic with fewest type parameters is considered best symbol to show in description.
            public INamedTypeSymbol BestGenericOverload { get; }

            public int Count { get; }

            public TypeOverloadInfo Aggregate(INamedTypeSymbol type)
            {
                if (type.Arity == 0)
                {
                    return new TypeOverloadInfo(type, BestGenericOverload, Count + 1);
                }

                // We consider generic with fewer type parameters better symbol to show in description.
                if (BestGenericOverload == null || type.Arity < BestGenericOverload.Arity)
                {
                    return new TypeOverloadInfo(NonGenericOverload, type, Count + 1);
                }

                return new TypeOverloadInfo(NonGenericOverload, BestGenericOverload, Count + 1);
            }
        }

        private readonly struct ReferenceCacheEntry
        {
            public ReferenceCacheEntry(
                Checksum checksum,
                bool includeInternalTypes,
                ImmutableArray<CompletionItem> cachedItems)
            {
                IncludeInternalTypes = includeInternalTypes;
                Checksum = checksum;
                CachedItems = cachedItems;
            }

            public Checksum Checksum { get; }

            public bool IncludeInternalTypes { get; }

            public ImmutableArray<CompletionItem> CachedItems { get; }
        }
    }
}
