﻿namespace ServiceControl.Infrastructure.RavenDB.Expiration
{

    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Threading;
    using Raven.Abstractions;
    using Raven.Abstractions.Commands;
    using Raven.Abstractions.Data;
    using Raven.Database;
    using Raven.Database.Impl;
    using Raven.Json.Linq;

    public static class AuditMessageCleaner
    {
        static NServiceBus.Logging.ILog logger = NServiceBus.Logging.LogManager.GetLogger(typeof(AuditMessageCleaner));

        public static void Clean(int deletionBatchSize, DocumentDatabase database, DateTime expiryThreshold)
        {
            using (DocumentCacher.SkipSettingDocumentsInDocumentCache())
            using (database.DisableAllTriggersForCurrentThread())
            using (var cts = new CancellationTokenSource())
            {
                var stopwatch = Stopwatch.StartNew();
                var documentWithCurrentThresholdTimeReached = false;
                var items = new List<ICommandData>(deletionBatchSize);
                var attachments = new List<string>(deletionBatchSize);
                try
                {
                    var query = new IndexQuery
                    {
                        Start = 0,
                        PageSize = deletionBatchSize,
                        Cutoff = SystemTime.UtcNow,
                        Query = "Status:3 OR Status:4",
                        FieldsToFetch = new[]
                        {
                            "__document_id",
                            "ProcessedAt",
                            "MessageMetadata"
                        },
                        SortedFields = new[]
                        {
                            new SortedField("ProcessedAt")
                            {
                                Field = "ProcessedAt",
                                Descending = false
                            }
                        },
                    };
                    var indexName = new ExpiryProcessedMessageIndex().IndexName;
                    database.Query(indexName, query, CancellationTokenSource.CreateLinkedTokenSource(database.WorkContext.CancellationToken, cts.Token).Token,
                        null,
                        doc =>
                        {
                            if (documentWithCurrentThresholdTimeReached)
                            {
                                return;
                            }

                            if (doc.Value<DateTime>("ProcessedAt") >= expiryThreshold)
                            {
                                documentWithCurrentThresholdTimeReached = true;
                                cts.Cancel();
                                return;
                            }

                            var id = doc.Value<string>("__document_id");
                            if (string.IsNullOrEmpty(id))
                            {
                                return;
                            }
                            items.Add(new DeleteCommandData
                            {
                                Key = id
                            });

                            string bodyId;
                            if (TryGetBodyId(doc, out bodyId))
                            {
                                attachments.Add(bodyId);
                            }
                        });
                }
                catch (OperationCanceledException)
                {
                    //Ignore
                }

                logger.DebugFormat("Batching deletion of {0} audit documents.", items.Count);
                var results = database.Batch(items);
                logger.DebugFormat("Batching deletion of {0} audit documents completed.", items.Count);

                database.TransactionalStorage.Batch(accessor =>
                {
                    logger.DebugFormat("Batching deletion of {0} attachment audit documents.", attachments.Count);

                    foreach (var attach in attachments)
                    {
                        accessor.Attachments.DeleteAttachment(attach, null);
                    }

                    logger.DebugFormat("Batching deletion of {0} attachment audit documents completed.", attachments.Count);

                });

                var deletionCount = results.Count(x => x.Deleted == true);

                if (deletionCount == 0)
                {
                    logger.Debug("No expired audit documents found");
                }
                else
                {
                    logger.InfoFormat("Deleted {0} expired audit documents. Batch execution took {1}ms", deletionCount, stopwatch.ElapsedMilliseconds);
                }
            }
        }

        static bool TryGetBodyId(RavenJObject doc, out string bodyId)
        {
            bodyId = null;
            var bodyNotStored = doc.SelectToken("MessageMetadata.BodyNotStored", errorWhenNoMatch: false);
            if (bodyNotStored != null && bodyNotStored.Value<bool>())
            {
                return false;
            }
            var messageId = doc.SelectToken("MessageMetadata.MessageId", errorWhenNoMatch: false);
            if (messageId == null)
            {
                return false;
            }
            bodyId = "messagebodies/" + messageId.Value<string>();
            return true;
        }
    }
}