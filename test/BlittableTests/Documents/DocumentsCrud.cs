﻿using System.Linq;
using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Raven.Server.Documents;
using Raven.Server.Json;
using Raven.Server.Json.Parsing;
using Xunit;

namespace BlittableTests.Documents
{
    public class DocumentsCrud : IDisposable
    {
        private DocumentsStorage _documentsStorage;
        private UnmanagedBuffersPool _unmanagedBuffersPool;

        public DocumentsCrud()
        {
            var configBuilder = new ConfigurationBuilder()
                .Add(new MemoryConfigurationProvider(new Dictionary<string, string>
                {
                    ["run.in.memory"] = "true"
                }));
            _documentsStorage = new DocumentsStorage("foo", configBuilder.Build());
            _documentsStorage.Initialize();
            _unmanagedBuffersPool = new UnmanagedBuffersPool("test");
        }

        [Theory]
        [InlineData("users/1")]
        [InlineData("USERs/1")]
        [InlineData("לכובע שלי שלוש פינות")]
        public void PutAndGetDocumentById(string key)
        {
            using (var ctx = new RavenOperationContext(_unmanagedBuffersPool))
            {
                ctx.Transaction = _documentsStorage.Environment.WriteTransaction();

                using (var doc = ctx.ReadObject(new DynamicJsonValue
                {
                    ["Name"] = key
                }, key, BlittableJsonDocumentBuilder.UsageMode.ToDisk))
                {
                    _documentsStorage.Put(ctx, key, null, doc);
                }
                ctx.Transaction.Commit();
            }

            using (var ctx = new RavenOperationContext(_unmanagedBuffersPool))
            {
                ctx.Transaction = _documentsStorage.Environment.WriteTransaction();

                var document = _documentsStorage.Get(ctx, key);
                Assert.NotNull(document);
                Assert.Equal(1, document.Etag);
                Assert.Equal(key, document.Key);
                string name;
                document.Data.TryGet("Name", out name);
                Assert.Equal(key, name);

                ctx.Transaction.Commit();
            }
        }

        [Fact]
        public void CanQueryByGlobalEtag()
        {
            using (var ctx = new RavenOperationContext(_unmanagedBuffersPool))
            {
                ctx.Transaction = _documentsStorage.Environment.WriteTransaction();

                using (var doc = ctx.ReadObject(new DynamicJsonValue
                {
                    ["Name"] = "Oren",
                    ["@metadata"] = new DynamicJsonValue
                    {
                        ["Raven-Entity-Name"] ="Users"
                    }
                }, "users/1", BlittableJsonDocumentBuilder.UsageMode.ToDisk))
                {
                    _documentsStorage.Put(ctx, "users/1", null, doc);
                }
                using (var doc = ctx.ReadObject(new DynamicJsonValue
                {
                    ["Name"] = "Ayende",
                    ["@metadata"] = new DynamicJsonValue
                    {
                        ["Raven-Entity-Name"] = "Users"
                    }
                }, "users/2", BlittableJsonDocumentBuilder.UsageMode.ToDisk))
                {
                    _documentsStorage.Put(ctx, "users/2", null, doc);
                }
                using (var doc = ctx.ReadObject(new DynamicJsonValue
                {
                    ["Name"] = "Arava",
                    ["@metadata"] = new DynamicJsonValue
                    {
                        ["Raven-Entity-Name"] = "Dogs"
                    }
                }, "pets/1", BlittableJsonDocumentBuilder.UsageMode.ToDisk))
                {
                    _documentsStorage.Put(ctx, "pets/1", null, doc);
                }
                ctx.Transaction.Commit();
            }

            using (var ctx = new RavenOperationContext(_unmanagedBuffersPool))
            {
                ctx.Transaction = _documentsStorage.Environment.WriteTransaction();

                var documents = _documentsStorage.GetDocumentsAfter(ctx, 0).ToList();
                Assert.Equal(3, documents.Count);
                string name;
                documents[0].Data.TryGet("Name", out name);
                Assert.Equal("Oren", name);
                documents[1].Data.TryGet("Name", out name);
                Assert.Equal("Ayende", name);
                documents[2].Data.TryGet("Name", out name);
                Assert.Equal("Arava", name);

                ctx.Transaction.Commit();
            }
        }

        [Fact]
        public void CanQueryByCollectionEtag()
        {
            using (var ctx = new RavenOperationContext(_unmanagedBuffersPool))
            {
                ctx.Transaction = _documentsStorage.Environment.WriteTransaction();

                using (var doc = ctx.ReadObject(new DynamicJsonValue
                {
                    ["Name"] = "Oren",
                    ["@metadata"] = new DynamicJsonValue
                    {
                        ["Raven-Entity-Name"] = "Users"
                    }
                }, "users/1", BlittableJsonDocumentBuilder.UsageMode.ToDisk))
                {
                    _documentsStorage.Put(ctx, "users/1", null, doc);
                }
              
                using (var doc = ctx.ReadObject(new DynamicJsonValue
                {
                    ["Name"] = "Arava",
                    ["@metadata"] = new DynamicJsonValue
                    {
                        ["Raven-Entity-Name"] = "Dogs"
                    }
                }, "pets/1", BlittableJsonDocumentBuilder.UsageMode.ToDisk))
                {
                    _documentsStorage.Put(ctx, "pets/1", null, doc);
                }
                using (var doc = ctx.ReadObject(new DynamicJsonValue
                {
                    ["Name"] = "Ayende",
                    ["@metadata"] = new DynamicJsonValue
                    {
                        ["Raven-Entity-Name"] = "Users"
                    }
                }, "users/2", BlittableJsonDocumentBuilder.UsageMode.ToDisk))
                {
                    _documentsStorage.Put(ctx, "users/2", null, doc);
                }
                ctx.Transaction.Commit();
            }

            using (var ctx = new RavenOperationContext(_unmanagedBuffersPool))
            {
                ctx.Transaction = _documentsStorage.Environment.WriteTransaction();

                var documents = _documentsStorage.GetDocumentsAfter(ctx, "Users", 0).ToList();
                Assert.Equal(2, documents.Count);
                string name;
                documents[0].Data.TryGet("Name", out name);
                Assert.Equal("Oren", name);
                documents[1].Data.TryGet("Name", out name);
                Assert.Equal("Ayende", name);

                ctx.Transaction.Commit();
            }
        }

        [Fact]
        public void WillVerifyEtags()
        {
            Assert.False(true);
        }

        public void Dispose()
        {
            _documentsStorage.Dispose();
            _unmanagedBuffersPool.Dispose();
        }
    }
}