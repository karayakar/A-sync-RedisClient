using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using TomLonghurst.RedisClient.Models.Commands;

namespace TomLonghurst.RedisClient.Models.Backlog
{
    public class BacklogItem<T> : IBacklogItem<T>
    {
        public Client.RedisClient RedisClient { get; set; }
        public IDuplexPipe Pipe { get; set; }
        public IRedisCommand RedisCommand { get; }
        public CancellationToken CancellationToken { get; }
        public async Task WriteAndSetResult()
        {
            if (CancellationToken.IsCancellationRequested)
            {
                TaskCompletionSource.TrySetCanceled();
                return;
            }

            try
            {
                RedisClient.LastUsed = DateTime.Now;

                var result =
                    await RedisClient.SendAndReceive_Impl(RedisCommand, ResultProcessor, CancellationToken, false);
                TaskCompletionSource.TrySetResult(result);
            }
            catch (OperationCanceledException)
            {
                TaskCompletionSource.TrySetCanceled();
            }
            catch (Exception e)
            {
                TaskCompletionSource.TrySetException(e);
            }
        }

        public void SetClientAndPipe(Client.RedisClient redisClient, IDuplexPipe pipe)
        {
            RedisClient = redisClient;
            Pipe = pipe;
        }

        public TaskCompletionSource<T> TaskCompletionSource { get; }
        public IResultProcessor<T> ResultProcessor { get; }

        public BacklogItem(IRedisCommand redisCommand, CancellationToken cancellationToken, TaskCompletionSource<T> taskCompletionSource, IResultProcessor<T> resultProcessor)
        {
            RedisCommand = redisCommand;
            CancellationToken = cancellationToken;
            TaskCompletionSource = taskCompletionSource;
            ResultProcessor = resultProcessor;
        }
    }
}