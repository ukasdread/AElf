using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AElf.Common;
using AElf.CrossChain.Cache;
using AElf.CrossChain.Cache.Exception;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Volo.Abp.EventBus;

namespace AElf.CrossChain.Grpc.Client
{
    public abstract class CrossChainGrpcClient<TResponse> : ILocalEventHandler<GrpcClientRequestIntervalUpdateEvent>, 
        IGrpcCrossChainClient where TResponse : IResponseIndexingMessage
    {
        private ILogger<CrossChainGrpcClient<TResponse>> Logger { get; }
        private int _initInterval;
        private int _adjustedInterval;
        private const int UnavailableConnectionInterval = 1_000;
        private readonly Channel _channel;
        private readonly ICrossChainDataProducer _crossChainDataProducer;
        protected CrossChainGrpcClient(Channel channel, ICrossChainDataProducer crossChainDataProducer)
        {
            _channel = channel;
            Logger = NullLogger<CrossChainGrpcClient<TResponse>>.Instance;
            _crossChainDataProducer = crossChainDataProducer;
            _adjustedInterval = _initInterval = UnavailableConnectionInterval;
        }

        private void UpdateRequestInterval(int initInterval) 
        {
            _initInterval = initInterval;
            _adjustedInterval = _initInterval;
        }

        public Task HandleEventAsync(GrpcClientRequestIntervalUpdateEvent receivedEventData)
        {
            UpdateRequestInterval(receivedEventData.Interval);
            return Task.CompletedTask;
        }
        
        /// <summary>
        /// Task to read response in loop.
        /// </summary>
        /// <param name="call"></param>
        /// <returns></returns>
        private Task ReadResponse(AsyncDuplexStreamingCall<RequestCrossChainBlockData, TResponse> call)
        {
            var responseReaderTask = Task.Run(async () =>
            {
                while (await call.ResponseStream.MoveNext())
                {
                    var response = call.ResponseStream.Current;

                    // requestCrossChain failed or useless response
                    if (!response.Success)
                    {
                        _adjustedInterval = AdjustInterval();
                        continue;
                    }
                    if(!_crossChainDataProducer.AddNewBlockInfo(response.BlockInfoResult))
                        continue;
                    
                    _adjustedInterval = _initInterval;
                    Logger.LogTrace(
                        $"Received response from chain {ChainHelpers.ConvertChainIdToBase58(response.BlockInfoResult.ChainId)} at height {response.Height}");
                }
            });
    
            return responseReaderTask;
        }

        private int AdjustInterval()
        {
            return Math.Min(_adjustedInterval * 2, UnavailableConnectionInterval);
        }

        /// <summary>
        /// Task to create requestCrossChain in loop.
        /// </summary>
        /// <param name="call"></param>
        /// <param name="cancellationToken"></param>
        /// <param name="chainId"></param>
        /// <returns></returns>
        private async Task RequestLoop(AsyncDuplexStreamingCall<RequestCrossChainBlockData, TResponse> call, 
            CancellationToken cancellationToken, int chainId)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var targetHeight = _crossChainDataProducer.GetChainHeightNeededForCache(chainId);
                    var request = new RequestCrossChainBlockData
                    {
                        SideChainId = chainId,
                        NextHeight = targetHeight
                    };
                    await call.RequestStream.WriteAsync(request);
                }
                catch (ChainCacheNotFoundException)
                {
                    Logger.LogWarning($"No cache for chain {ChainHelpers.ConvertChainIdToBase58(chainId)}");
                }
                finally
                {
                    await Task.Delay(_adjustedInterval);
                }
            }
        }

        /// <summary>
        /// Start to requestCrossChain one by one and also response one bye one.
        /// </summary>
        /// <param name="chainId"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task StartDuplexStreamingCall(int chainId, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using (var call = CallWithDuplexStreaming())
                {
                    while (_channel.State != ChannelState.Ready)
                    {
                        await _channel.WaitForStateChangedAsync(_channel.State);
                    }
                
                    try
                    {
                        // response reader task
                        var responseReaderTask = ReadResponse(call);

                        // requestCrossChain in loop
                        await RequestLoop(call, cancellationToken, chainId);
                        await responseReaderTask;
                    }
                    catch (RpcException e)
                    {
                        var status = e.Status.StatusCode;
                        if (status != StatusCode.Unavailable && status == StatusCode.DeadlineExceeded)
                        {
                            //var detail = e.Status.Detail;
                            //var task = StartDuplexStreamingCall(chainId, cancellationToken);
                            Logger.LogError(e, "Grpc cross chain client restarted with exception.");
                        }
                    }
                    finally
                    {
                        await call.RequestStream.CompleteAsync();
                    }
                }
            }
        }

        public abstract bool RequestIndexingCall(int chainId, int localListeningPort);

        protected abstract AsyncDuplexStreamingCall<RequestCrossChainBlockData, TResponse> CallWithDuplexStreaming(int milliSeconds = 0);
    }
    
    public class SideChainGrpcClient : CrossChainGrpcClient<ResponseSideChainBlockData>
    {
        private readonly CrossChainRpc.CrossChainRpcClient _client;

        public SideChainGrpcClient(Channel channel, CrossChainDataProducer crossChainDataProducer) : base(channel, crossChainDataProducer)
        {
            _client = new CrossChainRpc.CrossChainRpcClient(channel);
        }

        public override bool RequestIndexingCall(int chainId, int localListeningPort)
        {
            return false;
        }

        protected override AsyncDuplexStreamingCall<RequestCrossChainBlockData, ResponseSideChainBlockData> CallWithDuplexStreaming(int milliSeconds = 0)
        {
            return milliSeconds == 0
                ? _client.RequestSideChainDuplexStreaming()
                : _client.RequestSideChainDuplexStreaming(deadline: DateTime.UtcNow.AddMilliseconds(milliSeconds));
        }
    }
    
    public class ParentChainGrpcClient : CrossChainGrpcClient<ResponseParentChainBlockData>
    {
        private readonly CrossChainRpc.CrossChainRpcClient _client;

        public ParentChainGrpcClient(Channel channel, CrossChainDataProducer crossChainDataProducer) : base(channel, crossChainDataProducer)
        {
            _client = new CrossChainRpc.CrossChainRpcClient(channel);
        }

        public override bool RequestIndexingCall(int chainId, int localListeningPort)
        {
            try
            {
                return _client.RequestIndexing(new IndexingRequestMessage
                {
                    SideChainId = chainId,
                    ListeningPort = localListeningPort,
                    CertificateFileName = ChainHelpers.ConvertChainIdToBase58(chainId)
                    // use formatted chainId as certificate name, which can be changed later.  
                }).Result;
            }
            catch (RpcException)
            {
                return false;
            }
        }

        protected override AsyncDuplexStreamingCall<RequestCrossChainBlockData, ResponseParentChainBlockData> CallWithDuplexStreaming(int milliSeconds = 0)
        {
            return milliSeconds == 0
                ? _client.RequestParentChainDuplexStreaming()
                : _client.RequestParentChainDuplexStreaming(deadline: DateTime.UtcNow.AddMilliseconds(milliSeconds));
        }
    }

    public interface IGrpcCrossChainClient
    {
        Task StartDuplexStreamingCall(int chainId, CancellationToken cancellationToken);
        bool RequestIndexingCall(int chainId, int localListeningPort);
    }
}