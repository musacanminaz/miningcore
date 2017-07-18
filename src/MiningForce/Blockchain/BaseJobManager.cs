﻿using System;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using CodeContracts;
using Microsoft.Extensions.Logging;
using MiningForce.Authorization;
using MiningForce.Configuration;
using MiningForce.Configuration.Extensions;
using MiningForce.MininigPool;
using MiningForce.Stratum;

namespace MiningForce.Blockchain
{
    public abstract class BaseJobManager<TWorkerContext>
        where TWorkerContext: class, new()
    {
        protected BaseJobManager(IComponentContext ctx, ILogger logger, BlockchainDemon daemon)
        {
            this.ctx = ctx;
            this.logger = logger;
            this.daemon = daemon;
        }

        protected readonly IComponentContext ctx;
        protected BlockchainDemon daemon;
        protected StratumServer stratum;
        private IWorkerAuthorizer authorizer;
        protected PoolConfig poolConfig;
        protected readonly ILogger logger;

        protected readonly ConditionalWeakTable<StratumClient, TWorkerContext> workerContexts =
            new ConditionalWeakTable<StratumClient, TWorkerContext>();

        #region API-Surface

        public IObservable<object> Jobs { get; private set; }

        public async Task StartAsync(PoolConfig poolConfig, StratumServer stratum)
        {
            Contract.RequiresNonNull(poolConfig, nameof(poolConfig));
            Contract.RequiresNonNull(stratum, nameof(stratum));

            this.poolConfig = poolConfig;
            this.stratum = stratum;

            SetupAuthorizer();
            await StartDaemonAsync();
            await EnsureDaemonsSynchedAsync();
            await PostStartInitAsync();
            SetupJobPolling();

            logger.Info(() => $"[{poolConfig.Coin.Name}] Manager started");
        }

        #endregion // API-Surface

        protected virtual void SetupAuthorizer()
        {
            authorizer = ctx.ResolveNamed<IWorkerAuthorizer>(poolConfig.Authorizer.ToString());
        }

        /// <summary>
        /// Authorizes workers using configured authenticator
        /// </summary>
        /// <param name="worker">StratumClient requesting authorization</param>
        /// <param name="workername">Name of worker requesting authorization</param>
        /// <param name="password">The password</param>
        /// <returns></returns>
        public virtual Task<bool> HandleWorkerAuthenticateAsync(StratumClient worker, string workername, string password)
        {
            Contract.RequiresNonNull(worker, nameof(worker));
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(workername), $"{nameof(workername)} must not be empty");

            return authorizer.AuthorizeAsync((IBlockchainJobManager) this, worker.RemoteEndpoint, workername, password);
        }
        
        protected virtual async Task StartDaemonAsync()
        {
            await daemon.StartAsync(poolConfig);

            while (!await IsDaemonHealthy())
            {
                logger.Info(() => $"[{poolConfig.Coin.Name}] Waiting for daemons to come online ...");

                await Task.Delay(TimeSpan.FromSeconds(5));
            }

            logger.Info(() => $"[{poolConfig.Coin.Name}] All coin-daemons are online");
        }

        protected virtual void SetupJobPolling()
        {
            Jobs = Observable.Create<object>(observer =>
            {
                var interval = TimeSpan.FromMilliseconds(poolConfig.BlockRefreshInterval);
                var abort = false;

                var task = new Task(async () =>
                {
                    while (!abort)
                    {
                        try
                        {
                            // fetch params from daemon(s)
                            var start = DateTime.UtcNow;

                            if (await UpdateJobFromNetwork())
                            {
                                var jobParams = GetJobParamsForStratum();

                                // emit new value
                                observer.OnNext(jobParams);
                            }

                            // pause
                            var elapsed = DateTime.UtcNow - start;
                            var delta = interval - elapsed;

                            if (delta.TotalMilliseconds > 0)
                                await Task.Delay(delta);
                        }
                        catch (Exception ex)
                        {
                            logger.Warning(() => $"[{poolConfig.Coin.Name}] Error during job polling: {ex.Message}");
                        }
                    }
                }, TaskCreationOptions.LongRunning);

                task.Start();

                return Disposable.Create(()=> abort = true);
            })
            .Publish()
            .RefCount();
        }

        protected TWorkerContext GetWorkerContext(StratumClient client)
        {
            TWorkerContext context;

            lock (workerContexts)
            {
                if (!workerContexts.TryGetValue(client, out context))
                {
                    context = new TWorkerContext();
                    workerContexts.Add(client, context);
                }
            }

            return context;
        }

        /// <summary>
        /// Query coin-daemon for job (block) updates and returns true if a new job (block) was detected
        /// </summary>
        protected abstract Task<bool> IsDaemonHealthy();

        protected abstract Task EnsureDaemonsSynchedAsync();
        protected abstract Task PostStartInitAsync(); 

        /// <summary>
        /// Query coin-daemon for job (block) updates and returns true if a new job (block) was detected
        /// </summary>
        protected abstract Task<bool> UpdateJobFromNetwork();

        /// <summary>
        /// Packages current job parameters for stratum update
        /// </summary>
        /// <returns></returns>
        protected abstract object GetJobParamsForStratum();
    }
}
