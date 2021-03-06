using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lokad.Cqrs;
using Lokad.Cqrs.AtomicStorage;
using Lokad.Cqrs.Build;
using Lokad.Cqrs.Envelope;
using Lokad.Cqrs.Partition;
using Lokad.Cqrs.StreamingStorage;
using Lokad.Cqrs.TapeStorage;
using SaaS.Client;
using SaaS.Engine;

namespace SaaS.Wires
{
    public sealed class Setup
    {
        static readonly string FunctionalRecorderQueueName = Conventions.FunctionalEventRecorderQueue;
        static readonly string RouterQueueName = Conventions.DefaultRouterQueue;
        static readonly string ErrorsContainerName = Conventions.DefaultErrorsFolder;

        const string EventProcessingQueue = Conventions.Prefix + "-handle-events";
        const string AggregateHandlerQueue = Conventions.Prefix + "-handle-cmd-entity";
        string[] _serviceQueues;
        

        public void ConfigureQueues(int serviceQueueCount, int adapterQueueCount)
        {
            _serviceQueues = Enumerable
                .Range(0, serviceQueueCount)
                .Select((s, i) => Conventions.Prefix + "-handle-cmd-service-" + i)
                .ToArray();
        }

        public const string TapesContainer = Conventions.Prefix + "-tapes";

        public static readonly EnvelopeStreamer Streamer = Contracts.CreateStreamer();
        public static readonly IDocumentStrategy ViewStrategy = new ViewStrategy();
        public static readonly IDocumentStrategy DocStrategy = new DocumentStrategy();

        public IStreamRoot Streaming;

        public Func<string, IQueueWriter> QueueWriterFactory;
        public Func<string, IQueueReader> QueueReaderFactory;
        public Func<string, IAppendOnlyStore> AppendOnlyStoreFactory;
        public Func<IDocumentStrategy, IDocumentStore> DocumentStoreFactory;

        public Container Build()
        {
            var appendOnlyStore = AppendOnlyStoreFactory(TapesContainer);
            var messageStore = new MessageStore(appendOnlyStore, Streamer.MessageSerializer);

            var sendToRouterQueue = new MessageSender(Streamer, QueueWriterFactory(RouterQueueName));
            var sendToFunctionalRecorderQueue = new MessageSender(Streamer, QueueWriterFactory(FunctionalRecorderQueueName));
            var sendToEventProcessingQueue = new MessageSender(Streamer, QueueWriterFactory(EventProcessingQueue));

            var sendSmart = new TypedMessageSender(sendToRouterQueue, sendToFunctionalRecorderQueue);

            var store = new EventStore(messageStore);

            var quarantine = new EnvelopeQuarantine(Streamer, sendSmart, Streaming.GetContainer(ErrorsContainerName));

            var builder = new CqrsEngineBuilder(Streamer, quarantine);

            var events = new RedirectToDynamicEvent();
            var commands = new RedirectToCommand();
            var funcs = new RedirectToCommand();
            

            builder.Handle(QueueReaderFactory(EventProcessingQueue), aem => CallHandlers(events, aem), "watch");
            builder.Handle(QueueReaderFactory(AggregateHandlerQueue), aem => CallHandlers(commands, aem));
            builder.Handle(QueueReaderFactory(RouterQueueName), MakeRouter(messageStore), "watch");
            // multiple service queues
            _serviceQueues.ForEach(s => builder.Handle(QueueReaderFactory(s), aem => CallHandlers(funcs, aem)));

            builder.Handle(QueueReaderFactory(FunctionalRecorderQueueName), aem => RecordFunctionalEvent(aem, messageStore));
            var viewDocs = DocumentStoreFactory(ViewStrategy);
            var stateDocs = new NuclearStorage(DocumentStoreFactory(DocStrategy));



            var vector = new DomainIdentityGenerator(stateDocs);
            //var ops = new StreamOps(Streaming);
            var projections = new ProjectionsConsumingOneBoundedContext();
            // Domain Bounded Context
            DomainBoundedContext.EntityApplicationServices(viewDocs, store,vector).ForEach(commands.WireToWhen);
            DomainBoundedContext.FuncApplicationServices().ForEach(funcs.WireToWhen);
            DomainBoundedContext.Ports(sendSmart).ForEach(events.WireToWhen);
            DomainBoundedContext.Tasks(sendSmart, viewDocs, true).ForEach(builder.AddTask);
            projections.RegisterFactory(DomainBoundedContext.Projections);

            // Client Bounded Context
            projections.RegisterFactory(ClientBoundedContext.Projections);

            // wire all projections
            projections.BuildFor(viewDocs).ForEach(events.WireToWhen);

            // wire in event store publisher
            var publisher = new MessageStorePublisher(messageStore, sendToEventProcessingQueue, stateDocs, DoWePublishThisRecord);
            builder.AddTask(c => Task.Factory.StartNew(() => publisher.Run(c)));

            return new Container
            {
                Builder = builder,
                Setup = this,
                SendToCommandRouter = sendToRouterQueue,
                MessageStore = messageStore,
                ProjectionFactories = projections,
                ViewDocs = viewDocs,
                Publisher = publisher,
                AppendOnlyStore = appendOnlyStore
            };
        }

        static bool DoWePublishThisRecord(StoreRecord storeRecord)
        {
            return storeRecord.Key != "audit";
        }

        static void RecordFunctionalEvent(ImmutableEnvelope envelope, MessageStore store)
        {
            if (envelope.Message is IFuncEvent) store.RecordMessage("func", envelope);
            else throw new InvalidOperationException("Non-func event {0} landed to queue for tracking stateless events");
        }

        Action<ImmutableEnvelope> MakeRouter(MessageStore tape)
        {
            var entities = QueueWriterFactory(AggregateHandlerQueue);
            var processing = _serviceQueues.Select(QueueWriterFactory).ToArray();
            
            return envelope =>
            {
                var message = envelope.Message;
                if (message is ICommand)
                {
                    // all commands are recorded to audit stream, as they go through router
                    tape.RecordMessage("audit", envelope);
                }

                if (message is IEvent)
                {
                    throw new InvalidOperationException("Events are not expected in command router queue");
                }

                var data = Streamer.SaveEnvelopeData(envelope);

                if (message is ICommand<IIdentity>)
                {
                    entities.PutMessage(data);
                    return;
                }
                if (message is IFuncCommand)
                {
                    // randomly distribute between queues
                    var i = (Environment.TickCount & Int32.MaxValue) % processing.Length;
                    processing[i].PutMessage(data);
                    return;
                }
                throw new InvalidOperationException("Unknown queue format");
            };
        }



        /// <summary>
        /// Helper class that merely makes the concept explicit
        /// </summary>
        public sealed class ProjectionsConsumingOneBoundedContext
        {
            public delegate IEnumerable<object> FactoryForWhenProjections(IDocumentStore store);

            readonly IList<FactoryForWhenProjections> _factories = new List<FactoryForWhenProjections>();

            public void RegisterFactory(FactoryForWhenProjections factory)
            {
                _factories.Add(factory);
            }

            public IEnumerable<object> BuildFor(IDocumentStore store)
            {
                return _factories.SelectMany(factory => factory(store));
            }
        }

        static void CallHandlers(RedirectToDynamicEvent functions, ImmutableEnvelope aem)
        {
            var e = aem.Message as IEvent;

            if (e != null)
            {
                functions.InvokeEvent(e);
            }
        }

        static void CallHandlers(RedirectToCommand serviceCommands, ImmutableEnvelope aem)
        {

            var content = aem.Message;
            var watch = Stopwatch.StartNew();
            serviceCommands.Invoke(content);
            watch.Stop();

            var seconds = watch.Elapsed.TotalSeconds;
            if (seconds > 10)
            {
                SystemObserver.Notify("[Warn]: {0} took {1:0.0} seconds", content.GetType().Name, seconds);
            }
        }
    }

    public sealed class Container : IDisposable
    {
        public Setup Setup;
        public CqrsEngineBuilder Builder;
        public MessageSender SendToCommandRouter;
        public MessageStore MessageStore;
        public IAppendOnlyStore AppendOnlyStore;
        public IDocumentStore ViewDocs;
        public Setup.ProjectionsConsumingOneBoundedContext ProjectionFactories;
        public MessageStorePublisher Publisher;

        public CqrsEngineHost BuildEngine(CancellationToken token)
        {
            return Builder.Build(token);
        }

        public void ExecuteStartupTasks(CancellationToken token)
        {
            Publisher.VerifyEventStreamSanity();

            // we run S2 projections from 3 different BCs against one domain log
            StartupProjectionRebuilder.Rebuild(
                token,
                ViewDocs,
                MessageStore,
                store => ProjectionFactories.BuildFor(store));
        }

        public void Dispose()
        {
            using (AppendOnlyStore)
            {
                AppendOnlyStore = null;
            }
        }
    }

    public static class ExtendArrayEvil
    {
        public static void ForEach<T>(this IEnumerable<T> self, Action<T> action)
        {
            foreach (var variable in self)
            {
                action(variable);
            }
        }
    }
}