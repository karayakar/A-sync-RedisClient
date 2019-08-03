﻿using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using TomLonghurst.RedisClient.Exceptions;
using TomLonghurst.RedisClient.Extensions;
using TomLonghurst.RedisClient.Helpers;
using TomLonghurst.RedisClient.Models;
using TomLonghurst.RedisClient.Models.Backlog;
using TomLonghurst.RedisClient.Models.Commands;

namespace TomLonghurst.RedisClient.Client
{
    public partial class RedisClient : IDisposable
    {
        private static readonly Logger Log = new Logger();

        private long _outStandingOperations;

        public long OutstandingOperations => Interlocked.Read(ref _outStandingOperations);

        private long _operationsPerformed;

        private IDuplexPipe _pipe;
        private ReadResult _readResult;

        public object IsBusyLock = new object();
        public bool IsBusy;

        internal string LastAction;

        public long OperationsPerformed => Interlocked.Read(ref _operationsPerformed);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ValueTask<T> SendAndReceiveAsync<T>(IRedisCommand command,
            IResultProcessor<T> resultProcessor,
            CancellationToken cancellationToken,
            bool isReconnectionAttempt = false)
        {
            LastAction = "Throwing Cancelled Exception due to Cancelled Token";
            cancellationToken.ThrowIfCancellationRequested();

            Interlocked.Increment(ref _outStandingOperations);

            if (!isReconnectionAttempt)
            {
                bool isBusy;
                lock (IsBusyLock)
                {
                    isBusy = IsBusy;
                }

                if (isBusy)
                {
                    var taskCompletionSource = new TaskCompletionSource<T>();

                    var backlogQueueCount = _backlog.Count;

                    _backlog.Enqueue(new BacklogItem<T>(command, cancellationToken, taskCompletionSource, resultProcessor));

                    if (backlogQueueCount == 0)
                    {
                        StartBacklogProcessor();
                    }
                    
                    return new ValueTask<T>(taskCompletionSource.Task);
                }

                IsBusy = true;
            }

            return SendAndReceive_Impl(command, resultProcessor, cancellationToken, isReconnectionAttempt);
        }

        private async ValueTask<T> SendAndReceive_Impl<T>(IRedisCommand command, IResultProcessor<T> resultProcessor,
            CancellationToken cancellationToken, bool isReconnectionAttempt)
        {
            Log.Debug($"Executing Command: {command}");
            LastCommand = command;

            Interlocked.Increment(ref _operationsPerformed);

            try
            {
                if (!isReconnectionAttempt)
                {
                    if (!IsConnected)
                    {
                        await TryConnectAsync(cancellationToken).ConfigureAwait(false);
                    }
                }

                await Write(command);

                LastAction = "Reading Bytes Async";
                if (!_pipe.Input.TryRead(out _readResult))
                {
                    _readResult = await _pipe.Input.ReadAsync().ConfigureAwait(false);
                }
                
                return await resultProcessor.Start(this, _pipe, _readResult);
            }
            catch (Exception innerException)
            {
                if (innerException.IsSameOrSubclassOf(typeof(RedisException)) ||
                    innerException.IsSameOrSubclassOf(typeof(OperationCanceledException)))
                {
                    throw;
                }

                DisposeNetwork();
                IsConnected = false;
                throw new RedisConnectionException(innerException);
            }
            finally
            {
                Interlocked.Decrement(ref _outStandingOperations);
                if (!isReconnectionAttempt)
                {
                    lock (IsBusyLock)
                    {
                        IsBusy = false;
                    }
                }
            }
        }

        internal ValueTask<FlushResult> Write(IRedisCommand command)
        {
            var encodedCommandList = command.EncodedCommandList;
            
            LastAction = "Writing Bytes";
            var pipeWriter = _pipe.Output;
#if NETCORE
            
            foreach (var encodedCommand in encodedCommandList)
            {
                var bytesSpan = pipeWriter.GetSpan(encodedCommand.Length);
                encodedCommand.CopyTo(bytesSpan);
                pipeWriter.Advance(encodedCommand.Length);
            }

            return Flush();
#else
            return pipeWriter.WriteAsync(encodedCommandList.SelectMany(x => x).ToArray().AsMemory());
#endif
        }

        private ValueTask<FlushResult> Flush()
        {
            bool GetResult(FlushResult flush)
                // tell the calling code whether any more messages
                // should be written
                => !(flush.IsCanceled || flush.IsCompleted);

            async ValueTask<FlushResult> Awaited(ValueTask<FlushResult> incomplete)
                => await incomplete;

            // apply back-pressure etc
            var flushTask = _pipe.Output.FlushAsync();

            return flushTask.IsCompletedSuccessfully
                ? new ValueTask<FlushResult>(flushTask.Result)
                : Awaited(flushTask);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal async ValueTask<T> RunWithTimeout<T>(Func<CancellationToken, ValueTask<T>> action,
            CancellationToken originalCancellationToken)
        {
            originalCancellationToken.ThrowIfCancellationRequested();
            
            var cancellationTokenWithTimeout =
                CancellationTokenHelper.CancellationTokenWithTimeout(ClientConfig.Timeout,
                    originalCancellationToken);

            try
            {
                return await action.Invoke(cancellationTokenWithTimeout.Token);
            }
            catch (OperationCanceledException operationCanceledException)
            {
                throw TimeoutOrCancelledException(operationCanceledException, originalCancellationToken);
            }
            catch (SocketException socketException)
            {
                if (socketException.InnerException?.GetType().IsAssignableFrom(typeof(OperationCanceledException)) ==
                    true)
                {
                    throw TimeoutOrCancelledException(socketException.InnerException, originalCancellationToken);
                }

                throw;
            }
            finally
            {
                cancellationTokenWithTimeout.Dispose();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private async ValueTask RunWithTimeout(Func<CancellationToken, ValueTask> action,
            CancellationToken originalCancellationToken)
        {
            originalCancellationToken.ThrowIfCancellationRequested();

            var cancellationTokenWithTimeout =
                CancellationTokenHelper.CancellationTokenWithTimeout(ClientConfig.Timeout,
                    originalCancellationToken);

            try
            {
                await action.Invoke(cancellationTokenWithTimeout.Token);
            }
            catch (OperationCanceledException operationCanceledException)
            {
                throw TimeoutOrCancelledException(operationCanceledException, originalCancellationToken);
            }
            catch (SocketException socketException)
            {
                if (socketException.InnerException?.GetType().IsAssignableFrom(typeof(OperationCanceledException)) ==
                    true)
                {
                    throw TimeoutOrCancelledException(socketException.InnerException, originalCancellationToken);
                }

                throw;
            }
            finally
            {
                cancellationTokenWithTimeout.Dispose();
            }
        }

        private Exception TimeoutOrCancelledException(Exception exception, CancellationToken originalCancellationToken)
        {
            if (originalCancellationToken.IsCancellationRequested)
            {
                throw exception;
            }

            throw new RedisOperationTimeoutException(this);
        }
    }
}