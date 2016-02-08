﻿using System;
using System.IO;
using System.Threading.Tasks;
using Raven.Abstractions.Extensions;
using Raven.Server.Json;
using Raven.Server.Routing;
using Raven.Server.Json.Parsing;
using Constants = Raven.Abstractions.Data.Constants;

namespace Raven.Server.Documents
{
    public class BatchHandler : DatabaseRequestHandler
    {
        private struct CommandData
        {
            public string Method;
            public string Key;
            public BlittableJsonReaderObject Document;
            public BlittableJsonReaderObject AdditionalData;
            public long? Etag;
        }

        [RavenAction("/databases/*/bulk_docs", "POST")]
        public async Task BulkDocs()
        {
            RavenOperationContext context;
            using (ContextPool.AllocateOperationContext(out context))
            {
                BlittableJsonReaderArray commands;
                try
                {
                    commands = await context.ParseArrayToMemory(RequestBodyStream(), "bulk/docs",
                        // we will prepare the docs to disk in the actual PUT command
                        BlittableJsonDocumentBuilder.UsageMode.None);
                }
                catch (InvalidDataException)
                {
                    throw;
                }
                catch (Exception ioe)
                {
                    throw new InvalidDataException("Could not parse json", ioe);
                }

                var parsedCommands = new CommandData[commands.Length];

                for (int i = 0; i < commands.Count; i++)
                {
                    var cmd = commands.GetByIndex<BlittableJsonReaderObject>(i);

                    if (cmd.TryGet("Method", out parsedCommands[i].Method) == false)
                        throw new InvalidDataException("Missing 'Method' property");
                    if (cmd.TryGet("Key", out parsedCommands[i].Key) == false)
                        throw new InvalidDataException("Missing 'Key' property");

                    // optional
                    cmd.TryGet("ETag", out parsedCommands[i].Etag);
                    cmd.TryGet("AdditionalData", out parsedCommands[i].
                        AdditionalData);

                    // We have to do additional processing on the documents
                    // in particular, prepare them for disk by compressing strings, validating floats, etc

                    // We **HAVE** to do that outside of the write transaction lock, that is why we are handling
                    // it in this manner, first parse the commands, then prepare for the put, finally open
                    // the transaction and actually write
                    switch (parsedCommands[i].Method)
                    {
                        case "PUT":
                            BlittableJsonReaderObject doc;
                            if (cmd.TryGet("Document", out doc) == false)
                                throw new InvalidDataException("Missing 'Document' property");

                            // we need to split this document to an independent blittable document
                            // and this time, we'll prepare it for disk.

                            DynamicJsonValue mutableMetadata;
                            BlittableJsonReaderObject metadata;
                            if (doc.TryGet(Constants.Metadata, out metadata))
                            {
                                metadata.Modifications = mutableMetadata = new DynamicJsonValue(metadata);
                            }
                            else
                            {
                                doc.Modifications = new DynamicJsonValue(doc)
                                {
                                    [Constants.Metadata] = mutableMetadata = new DynamicJsonValue()
                                };
                            }

                            mutableMetadata["Raven-Last-Modified"] = DateTime.UtcNow.GetDefaultRavenFormat();

                            parsedCommands[i].Document = await context.ReadObject(doc, parsedCommands[i].Key,
                                BlittableJsonDocumentBuilder.UsageMode.ToDisk);
                            break;

                    }
                }

                var reply = new DynamicJsonArray();

                using (context.Transaction = context.Environment.WriteTransaction())
                {

                    for (int i = 0; i < parsedCommands.Length; i++)
                    {
                        switch (parsedCommands[i].Method)
                        {
                            case "PUT":
                                var newEtag = DocumentsStorage.Put(context, parsedCommands[i].Key, parsedCommands[i].Etag,
                                    parsedCommands[i].Document);

                                BlittableJsonReaderObject metadata;
                                parsedCommands[i].Document.TryGet(Constants.Metadata, out metadata);

                                reply.Add(new DynamicJsonValue
                                {
                                    ["Key"] = parsedCommands[i].Key,
                                    ["Etag"] = newEtag,
                                    ["Method"] = "PUT",
                                    ["AdditionalData"] = parsedCommands[i].AdditionalData,
                                    ["Metadata"] = metadata
                                });
                                break;
                            case "DELETE":
                                var deleted = DocumentsStorage.Delete(context, parsedCommands[i].Key, parsedCommands[i].Etag);
                                reply.Add(new DynamicJsonValue
                                {
                                    ["Key"] = parsedCommands[i].Key,
                                    ["Method"] = "DELETE",
                                    ["AdditionalData"] = parsedCommands[i].AdditionalData,
                                    ["Deleted"] = deleted
                                });
                                break;
                        }
                    }

                    context.Transaction.Commit();
                }

                HttpContext.Response.StatusCode = 201;

                var writer = new BlittableJsonTextWriter(context, ResponseBodyStream());
                await context.WriteAsync(writer, reply);
                writer.Flush();
            }
        }
    }
}

      