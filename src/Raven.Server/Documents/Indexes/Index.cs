﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Raven.Abstractions.Data;
using Raven.Abstractions.Extensions;
using Raven.Server.Documents.Indexes.Auto;
using Raven.Server.Documents.Indexes.Persistance.Lucene;
using Raven.Server.Documents.Queries;
using Raven.Server.Json;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Context;

using Voron;

namespace Raven.Server.Documents.Indexes
{
    public abstract class Index<TIndexDefinition> : Index
        where TIndexDefinition : IndexDefinitionBase
    {
        public new TIndexDefinition Definition => (TIndexDefinition)base.Definition;

        protected Index(int indexId, IndexType type, TIndexDefinition definition)
            : base(indexId, type, definition)
        {
        }
    }

    public abstract class Index : IDisposable
    {
        private static readonly string EtagsMap = "Etags.Map";

        private static readonly string EtagsTombstone = "Etags.Tombstone";

        private static readonly Slice TypeSlice = "Type";

        protected readonly LuceneIndexPersistance IndexPersistance;

        private readonly object _locker = new object();

        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        protected DocumentDatabase DocumentDatabase;

        private Task _indexingTask;

        private bool _initialized;

        private UnmanagedBuffersPool _unmanagedBuffersPool;

        private StorageEnvironment _environment;

        private TransactionContextPool _contextPool;

        private bool _disposed;

        private readonly ManualResetEventSlim _mre = new ManualResetEventSlim();

        protected Index(int indexId, IndexType type, IndexDefinitionBase definition)
        {
            if (indexId <= 0)
                throw new ArgumentException("IndexId must be greater than zero.", nameof(indexId));

            IndexId = indexId;
            Type = type;
            Definition = definition;
            IndexPersistance = new LuceneIndexPersistance(indexId, definition);
            Collections = new HashSet<string>(Definition.Collections, StringComparer.OrdinalIgnoreCase);
        }

        public static Index Open(int indexId, DocumentDatabase documentDatabase)
        {
            var options = StorageEnvironmentOptions.ForPath(Path.Combine(documentDatabase.Configuration.Indexing.IndexStoragePath, indexId.ToString()));
            try
            {
                options.SchemaVersion = 1;

                var environment = new StorageEnvironment(options);
                using (var tx = environment.ReadTransaction())
                {
                    var statsTree = tx.ReadTree("Stats");
                    var result = statsTree.Read(TypeSlice);
                    if (result == null)
                        throw new InvalidOperationException($"Stats tree does not contain 'Type' entry in index '{indexId}'.");

                    var type = (IndexType)result.Reader.ReadLittleEndianInt32();

                    switch (type)
                    {
                        case IndexType.Auto:
                            return AutoIndex.Open(indexId, environment, documentDatabase);
                        default:
                            throw new NotImplementedException();
                    }
                }
            }
            catch (Exception)
            {
                options.Dispose();
                throw;
            }
        }

        public int IndexId { get; }

        public IndexType Type { get; }

        public IndexDefinitionBase Definition { get; }

        public string Name => Definition?.Name;

        public bool ShouldRun { get; private set; } = true;

        protected void Initialize(DocumentDatabase documentDatabase)
        {
            lock (_locker)
            {
                if (_initialized)
                    throw new InvalidOperationException($"Index '{Name} ({IndexId})' was already initialized.");

                var options = documentDatabase.Configuration.Indexing.RunInMemory
                    ? StorageEnvironmentOptions.CreateMemoryOnly()
                    : StorageEnvironmentOptions.ForPath(Path.Combine(documentDatabase.Configuration.Indexing.IndexStoragePath, IndexId.ToString()));

                options.SchemaVersion = 1;

                try
                {
                    Initialize(new StorageEnvironment(options), documentDatabase);
                }
                catch (Exception)
                {
                    options.Dispose();
                    throw;
                }
            }
        }

        protected unsafe void Initialize(StorageEnvironment environment, DocumentDatabase documentDatabase)
        {
            if (_disposed)
                throw new ObjectDisposedException($"Index '{Name} ({IndexId})' was already disposed.");

            lock (_locker)
            {
                if (_initialized)
                    throw new InvalidOperationException($"Index '{Name} ({IndexId})' was already initialized.");

                try
                {
                    Debug.Assert(Definition != null);

                    DocumentDatabase = documentDatabase;
                    _environment = environment;
                    _unmanagedBuffersPool = new UnmanagedBuffersPool($"Indexes//{IndexId}");
                    _contextPool = new TransactionContextPool(_unmanagedBuffersPool, _environment);

                    TransactionOperationContext context;
                    using (_contextPool.AllocateOperationContext(out context))
                    using (var tx = context.OpenWriteTransaction())
                    {
                        var typeInt = (int)Type;

                        var statsTree = tx.InnerTransaction.CreateTree("Stats");
                        statsTree.Add(TypeSlice, new Slice((byte*)&typeInt, sizeof(int)));

                        tx.InnerTransaction.CreateTree(EtagsMap);
                        tx.InnerTransaction.CreateTree(EtagsTombstone);

                        Definition.Persist(context);

                        tx.Commit();
                    }

                    IndexPersistance.Initialize(DocumentDatabase.Configuration.Indexing);

                    _initialized = true;
                }
                catch (Exception)
                {
                    Dispose();
                    throw;
                }
            }
        }

        public void Execute(CancellationToken cancellationToken)
        {
            if (_disposed)
                throw new ObjectDisposedException($"Index '{Name} ({IndexId})' was already disposed.");

            if (_initialized == false)
                throw new InvalidOperationException($"Index '{Name} ({IndexId})' was not initialized.");

            lock (_locker)
            {
                if (_indexingTask != null)
                    throw new InvalidOperationException($"Index '{Name} ({IndexId})' is executing.");

                _indexingTask = Task.Factory.StartNew(() => ExecuteIndexing(cancellationToken), TaskCreationOptions.LongRunning);
            }
        }

        public void Dispose()
        {
            lock (_locker)
            {
                if (_disposed)
                    throw new ObjectDisposedException($"Index '{Name} ({IndexId})' was already disposed.");

                _disposed = true;

                _cancellationTokenSource.Cancel();

                _indexingTask?.Wait();
                _indexingTask = null;

                _environment?.Dispose();
                _environment = null;

                _unmanagedBuffersPool?.Dispose();
                _unmanagedBuffersPool = null;

                _contextPool?.Dispose();
                _contextPool = null;
            }
        }

        protected HashSet<string> Collections;

        protected abstract bool IsStale(DocumentsOperationContext databaseContext, TransactionOperationContext indexContext);

        /// <summary>
        /// This should only be used for testing purposes.
        /// </summary>
        internal Dictionary<string, long> GetLastMappedEtags()
        {
            TransactionOperationContext context;
            using (_contextPool.AllocateOperationContext(out context))
            {
                using (var tx = context.OpenReadTransaction())
                {
                    var etags = new Dictionary<string, long>();
                    foreach (var collection in Collections)
                    {
                        etags[collection] = ReadLastMappedEtag(tx, collection);
                    }

                    return etags;
                }
            }
        }

        /// <summary>
        /// This should only be used for testing purposes.
        /// </summary>
        internal Dictionary<string, long> GetLastTombstoneEtags()
        {
            TransactionOperationContext context;
            using (_contextPool.AllocateOperationContext(out context))
            {
                using (var tx = context.OpenReadTransaction())
                {
                    var etags = new Dictionary<string, long>();
                    foreach (var collection in Collections)
                    {
                        etags[collection] = ReadLastTombstoneEtag(tx, collection);
                    }

                    return etags;
                }
            }
        }

        protected long ReadLastTombstoneEtag(RavenTransaction tx, string collection)
        {
            return ReadLastEtag(tx, EtagsTombstone, collection);
        }

        protected long ReadLastMappedEtag(RavenTransaction tx, string collection)
        {
            return ReadLastEtag(tx, EtagsMap, collection);
        }

        private static long ReadLastEtag(RavenTransaction tx, string tree, string collection)
        {
            var statsTree = tx.InnerTransaction.CreateTree(tree);
            var readResult = statsTree.Read(collection);
            long lastEtag = 0;
            if (readResult != null)
                lastEtag = readResult.Reader.ReadLittleEndianInt64();

            return lastEtag;
        }

        private static void WriteLastTombstoneEtag(RavenTransaction tx, string collection, long etag)
        {
            WriteLastEtag(tx, EtagsTombstone, collection, etag);
        }

        private static void WriteLastMappedEtag(RavenTransaction tx, string collection, long etag)
        {
            WriteLastEtag(tx, EtagsMap, collection, etag);
        }

        private static unsafe void WriteLastEtag(RavenTransaction tx, string tree, string collection, long etag)
        {
            var statsTree = tx.InnerTransaction.CreateTree(tree);
            statsTree.Add(collection, new Slice((byte*)&etag, sizeof(long)));
        }

        private void ExecuteIndexing(CancellationToken cancellationToken)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellationTokenSource.Token))
            {
                try
                {
                    DocumentDatabase.Notifications.OnDocumentChange += HandleDocumentChange;

                    while (ShouldRun)
                    {
                        try
                        {
                            _mre.Reset();

                            cts.Token.ThrowIfCancellationRequested();

                            ExecuteCleanup(cts.Token);
                            ExecuteMap(cts.Token);

                            _mre.Wait(cts.Token);
                        }
                        catch (OutOfMemoryException )
                        {
                            // TODO
                        }
                        catch (AggregateException )
                        {
                            // TODO
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                        catch (Exception )
                        {
                            // TODO
                        }
                    }
                }
                finally
                {
                    DocumentDatabase.Notifications.OnDocumentChange -= HandleDocumentChange;
                }
            }
        }

        private void ExecuteCleanup(CancellationToken token)
        {
            DocumentsOperationContext databaseContext;
            TransactionOperationContext indexContext;

            using (DocumentDatabase.DocumentsStorage.ContextPool.AllocateOperationContext(out databaseContext))
            using (_contextPool.AllocateOperationContext(out indexContext))
            {
                var pageSize = DocumentDatabase.Configuration.Indexing.MaxNumberOfTombstonesToFetch;

                foreach (var collection in Collections)
                {
                    long lastMappedEtag;
                    long lastTombstoneEtag;
                    using (indexContext.OpenReadTransaction())
                    {
                        lastMappedEtag = ReadLastMappedEtag(indexContext.Transaction, collection);
                        lastTombstoneEtag = ReadLastTombstoneEtag(indexContext.Transaction, collection);
                    }

                    var lastEtag = lastTombstoneEtag;
                    var count = 0;

                    using (var indexActions = IndexPersistance.Write())
                    {
                        using (databaseContext.OpenReadTransaction())
                        {
                            var sw = Stopwatch.StartNew();
                            foreach (var tombstone in DocumentDatabase.DocumentsStorage.GetTombstonesAfter(databaseContext, collection, lastEtag + 1, 0, pageSize))
                            {
                                token.ThrowIfCancellationRequested();

                                count++;
                                lastEtag = tombstone.Etag;

                                if (tombstone.DeletedEtag > lastMappedEtag)
                                    continue; // no-op, we have not yet indexed this document

                                indexActions.Delete(tombstone.Key);

                                if (sw.Elapsed > DocumentDatabase.Configuration.Indexing.TombstoneProcessingTimeout.AsTimeSpan)
                                {
                                    break;
                                }
                            }
                        }
                    }

                    if (count == 0)
                        return;

                    if (lastEtag <= lastTombstoneEtag)
                        return;

                    using (var tx = indexContext.OpenWriteTransaction())
                    {
                        WriteLastTombstoneEtag(tx, collection, lastEtag);

                        tx.Commit();
                    }

                    _mre.Set(); // might be more
                }
            }
        }

        private void HandleDocumentChange(DocumentChangeNotification notification)
        {
            if (Collections.Contains(notification.CollectionName) == false)
                return;

            _mre.Set();
        }

        private void ExecuteMap(CancellationToken cancellationToken)
        {
            DocumentsOperationContext databaseContext;
            TransactionOperationContext indexContext;

            using (DocumentDatabase.DocumentsStorage.ContextPool.AllocateOperationContext(out databaseContext))
            using (_contextPool.AllocateOperationContext(out indexContext))
            {
                if (IsStale(databaseContext, indexContext) == false)
                    return;

                var pageSize = DocumentDatabase.Configuration.Indexing.MaxNumberOfDocumentsToFetchForMap;

                foreach (var collection in Collections)
                {
                    long lastMappedEtag;
                    using (indexContext.OpenReadTransaction())
                    {
                        lastMappedEtag = ReadLastMappedEtag(indexContext.Transaction, collection);
                    }

                    var lastEtag = lastMappedEtag;
                    var count = 0;

                    using (var indexActions = IndexPersistance.Write())
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        using (databaseContext.OpenReadTransaction())
                        {
                            var sw = Stopwatch.StartNew();
                            foreach (var document in DocumentDatabase.DocumentsStorage.GetDocumentsAfter(databaseContext, collection, lastEtag + 1, 0, pageSize))
                            {
                                cancellationToken.ThrowIfCancellationRequested();

                                count++;
                                lastEtag = document.Etag;

                                try
                                {
                                    indexActions.Write(document);
                                }
                                catch (Exception )
                                {
                                    // TODO [ppekrol] log?
                                    continue;
                                }

                                if (sw.Elapsed > DocumentDatabase.Configuration.Indexing.DocumentProcessingTimeout.AsTimeSpan)
                                {
                                    break;
                                }
                            }
                        }
                    }

                    if (count == 0)
                        return;

                    if (lastEtag <= lastMappedEtag)
                        return;

                    using (var tx = indexContext.OpenWriteTransaction())
                    {
                        WriteLastMappedEtag(tx, collection, lastEtag);

                        tx.Commit();
                    }

                    _mre.Set(); // might be more
                }
            }
        }

        public DocumentQueryResult Query(IndexQuery query, DocumentsOperationContext context, CancellationToken token)
        {
            if (_disposed)
                throw new ObjectDisposedException($"Index '{Name} ({IndexId})' was already disposed.");

            TransactionOperationContext indexContext;
            var result = new DocumentQueryResult()
            {
                IndexName = Name
            };

            using (_contextPool.AllocateOperationContext(out indexContext))
            {
                result.IsStale = IsStale(context, indexContext);
            }

            Reference<int> totalResults = new Reference<int>();
            List<string> documentIds;
            using (var indexRead = IndexPersistance.Read())
            {
                documentIds = indexRead.Query(query, token, totalResults).ToList();
            }

            result.TotalResults = totalResults.Value;

            context.OpenReadTransaction();

            foreach (var id in documentIds)
            {
                var document = DocumentDatabase.DocumentsStorage.Get(context, id);

                result.Results.Add(document);
            }

            return result;
        }
    }
}