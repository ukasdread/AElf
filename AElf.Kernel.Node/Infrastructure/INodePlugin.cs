using System.Threading.Tasks;

namespace AElf.Kernel.Node.Infrastructure
{
    public interface INodePlugin
    {
        Task StartAsync();
        Task ShutdownAsync();
    }
}