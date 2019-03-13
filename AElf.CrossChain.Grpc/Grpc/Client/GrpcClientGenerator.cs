using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AElf.Common;
using AElf.CrossChain.Cache;
using AElf.CrossChain.Grpc.Exceptions;
using AElf.Cryptography.Certificate;
using Grpc.Core;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EventBus;
using Volo.Abp.EventBus.Local;

namespace AElf.CrossChain.Grpc.Client
{
    public class GrpcClientGenerator : ISingletonDependency
    {
        private CancellationTokenSource TokenSourceToSideChain { get; } = new CancellationTokenSource();
        private CancellationTokenSource TokenSourceToParentChain { get; } = new CancellationTokenSource();
        private readonly CrossChainDataProducer _crossChainDataProducer;
        private ILocalEventBus LocalEventBus { get; }

        public GrpcClientGenerator(CrossChainDataProducer crossChainDataProducer)
        {
            _crossChainDataProducer = crossChainDataProducer;
            LocalEventBus = NullLocalEventBus.Instance;
        }

        /// <summary>
        /// Extend interval for request after initial block synchronization.
        /// </summary>
        public void UpdateRequestInterval(int interval)
        {
            // no wait
            LocalEventBus.PublishAsync(new GrpcClientRequestIntervalUpdateEvent
            {
                Interval = interval
            });
        }

        #region Create client

        public void CreateClient(ICrossChainCommunicationContext crossChainCommunicationContext, string certificate)
        {
            var client = CreateGrpcClient((GrpcCrossChainCommunicationContext)crossChainCommunicationContext, certificate);
            //client = clientBasicInfo.TargetIsSideChain ? (ClientToSideChain) client : (ClientToParentChain) client;
            var connectingResult = client.RequestIndexingCall(crossChainCommunicationContext.LocalChainId,
                ((GrpcCrossChainCommunicationContext) crossChainCommunicationContext).LocalListeningPort);
            if (!connectingResult)
                return;
            
            client.StartDuplexStreamingCall(crossChainCommunicationContext.RemoteChainId, crossChainCommunicationContext.RemoteIsSideChain
                ? TokenSourceToSideChain.Token
                : TokenSourceToParentChain.Token);
        }

        /// <summary>
        /// Create a new client to parent chain 
        /// </summary>
        /// <returns>
        /// </returns>    
        private IGrpcCrossChainClient CreateGrpcClient(GrpcCrossChainCommunicationContext grpcClientBase, string certificate)
        {
            var channel = CreateChannel(grpcClientBase.ToUriStr(), grpcClientBase.RemoteChainId, certificate);

            if (grpcClientBase.RemoteIsSideChain)
            {
                var clientToSideChain = new SideChainGrpcClient(channel, _crossChainDataProducer);
                return clientToSideChain;
            }

            return new ParentChainGrpcClient(channel, _crossChainDataProducer);
        }

        /// <summary>
        /// Create a new channel
        /// </summary>
        /// <param name="uriStr"></param>
        /// <param name="targetChainId"></param>
        /// <param name="crt">Certificate</param>
        /// <returns></returns>
        /// <exception cref="CertificateException"></exception>
        private Channel CreateChannel(string uriStr, int targetChainId, string crt)
        {
            var channelCredentials = new SslCredentials(crt);
            var channel = new Channel(uriStr, channelCredentials);
            return channel;
        }

        #endregion

        /// <summary>
        /// Close and clear clients to side chain
        /// </summary>
        public void CloseClientsToSideChain()
        {
            TokenSourceToSideChain?.Cancel();
            TokenSourceToSideChain?.Dispose();
        }

        /// <summary>
        /// close and clear clients to parent chain
        /// </summary>
        public void CloseClientToParentChain()
        {
            TokenSourceToParentChain?.Cancel();
            TokenSourceToParentChain?.Dispose();
        }
    }
}