using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using TomLonghurst.RedisClient.Models.Commands;

namespace TomLonghurst.RedisClient.Models.Backlog
{
    public interface IBacklog
    {
        Client.RedisClient RedisClient { get; }
        IDuplexPipe Pipe { get; }
        
        IRedisCommand RedisCommand { get; }
        CancellationToken CancellationToken { get; }
        Task WriteAndSetResult();
        void SetClientAndPipe(Client.RedisClient redisClient, IDuplexPipe pipe);
    }
    public interface IBacklogItem : IBacklog
    {
        TaskCompletionSource<bool> TaskCompletionSource { get; }
    }
    
    public interface IBacklogItem<T> : IBacklog
    {
        TaskCompletionSource<T> TaskCompletionSource { get; }
        
        IResultProcessor<T> ResultProcessor { get; }
    }
}