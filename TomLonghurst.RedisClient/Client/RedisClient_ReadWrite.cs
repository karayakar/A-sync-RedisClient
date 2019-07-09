﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TomLonghurst.RedisClient.Exceptions;
using TomLonghurst.RedisClient.Extensions;
using TomLonghurst.RedisClient.Helpers;

namespace TomLonghurst.RedisClient.Client
{
    public partial class RedisClient : IDisposable
    {
        private static readonly Logger Log = new Logger();
        
        private readonly SemaphoreSlim _sendSemaphoreSlim = new SemaphoreSlim(1, 1);

        private long _outStandingOperations;

        public long OutstandingOperations => Interlocked.Read(ref _outStandingOperations);
        
        private long _operationsPerformed;

        public long OperationsPerformed => Interlocked.Read(ref _operationsPerformed);

        public bool IsConnected
        {
            get
            {
                try
                {
                    if (_socket == null || _socket.IsDisposed)
                    {
                        return false;
                    }

                    return !(_socket.Poll(1, SelectMode.SelectRead) && _socket.Available == 0);
                }
                catch (SocketException)
                {
                    return false;
                }
            }
        }

        private Task<T> SendAndReceiveAsync<T>(string command,
            Func<T> responseReader,
            CancellationToken cancellationToken = default)
        {
            Log.Debug($"Executing Command: {command}");

            return SendAndReceiveAsync(command.ToUtf8Bytes(), responseReader, cancellationToken);
        }

        private async Task<T> SendAndReceiveAsync<T>(byte[] bytes,
            Func<T> responseReader,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await TryConnectAsync(cancellationToken);

            Interlocked.Increment(ref _outStandingOperations);

            await _sendSemaphoreSlim.WaitAsync(cancellationToken);

            Interlocked.Increment(ref _operationsPerformed);
            
            try
            {
                if (bytes.Length >= 1024 * 1024)
                {
                    await _bufferedStream.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
                }
                else
                {
                    _bufferedStream.Write(bytes, 0, bytes.Length);
                }

                return responseReader.Invoke();
            }
            finally
            {
                Interlocked.Decrement(ref _outStandingOperations);
                _sendSemaphoreSlim.Release();
            }
        }

        private Task ExpectSuccess()
        {
            var response = ReadLine();
            if (response.StartsWith("-"))
            {
                throw new RedisFailedCommandException(response);
            }

            return Task.CompletedTask;
        }

        private async Task<string> ExpectData()
        {
            return (await ReadData()).FromUtf8();
        }
        
        private string ExpectWord()
        {
            var word = ReadLine();

            if (!word.StartsWith("+"))
            {
                throw new UnexpectedRedisResponseException(word);
            }

            return word.Substring(1);
        }
        
        private int ExpectNumber()
        {
            var line = ReadLine();

            if (!line.StartsWith(":") || !int.TryParse(line.Substring(1), out var number))
            {
                throw new UnexpectedRedisResponseException(line);
            }

            return number;
        }

        private async Task<IEnumerable<string>> ExpectArray()
        {
            var arrayWithCountLine = ReadLine();

            if (!arrayWithCountLine.StartsWith("*"))
            {
                throw new UnexpectedRedisResponseException(arrayWithCountLine);
            }

            if (!int.TryParse(arrayWithCountLine.Substring(1), out var count))
            {
                throw new UnexpectedRedisResponseException("Error getting message count");
            }

            var results = new byte [count][];
            for (var i = 0; i < count; i++)
            {
                results[i] = await ReadData();
            }

            return results.FromUtf8();
        }
        
        private async Task<byte[]> ReadData()
        {
            var line = ReadLine();

            if (string.IsNullOrWhiteSpace(line))
            {
                throw new UnexpectedRedisResponseException("Zero Length Response from Redis");
            }

            var firstChar = line.First();

            if (firstChar == '-')
            {
                throw new RedisFailedCommandException(line);
            }

            if (firstChar == '$')
            {
                if (line == "$-1")
                {
                    return null;
                }

                if (int.TryParse (line.Substring(1), out var byteSizeOfData)){
                    var byteBuffer = new byte [byteSizeOfData];

                    var bytesRead = 0;
                    do {
                        int read;
                        if (byteSizeOfData >= 1024 * 1024)
                        {
                            read = await _bufferedStream.ReadAsync(byteBuffer, bytesRead, byteSizeOfData - bytesRead);
                        }
                        else
                        {
                            read = _bufferedStream.Read(byteBuffer, bytesRead, byteSizeOfData - bytesRead);
                        }

                        if (read < 1)
                        {
                            throw new UnexpectedRedisResponseException($"Invalid termination mid stream: {byteBuffer.FromUtf8()}");
                        }

                        bytesRead += read; 
                    }
                    while (bytesRead < byteSizeOfData);

                    if (_bufferedStream.ReadByte() != '\r' || _bufferedStream.ReadByte() != '\n')
                    {
                        throw new UnexpectedRedisResponseException($"Invalid termination: {byteBuffer.FromUtf8()}");
                    }

                    return byteBuffer;
                }
                
                throw new UnexpectedRedisResponseException("Invalid length");
            }
            
            throw new UnexpectedRedisResponseException ($"Unexpected reply: {line}");
        }

        private string ReadLine()
        {
            var stringBuilder = new StringBuilder ();
            int c;
		
            while ((c = _bufferedStream.ReadByte ()) != -1){
                if (c == '\r')
                    continue;
                if (c == '\n')
                    break;
                stringBuilder.Append ((char) c);
            }
            return stringBuilder.ToString ();
        }

        private async Task TryConnectAsync(CancellationToken cancellationToken)
        {
            if (IsConnected)
            {
                return;
            }
            
            try
            {
                await Task.Run(async () => await ConnectAsync(), cancellationToken);
            }
            catch (Exception innerException)
            {
                throw new RedisConnectionException(innerException);
            }
        }

        private async Task<T> RunWithTimeout<T>(Func<T> action, CancellationToken originalCancellationToken)
        {
            originalCancellationToken.ThrowIfCancellationRequested();

            try
            {
                var cancellationTokenWithTimeout =
                    CancellationTokenHelper.CancellationTokenWithTimeout(_redisClientConfig.Timeout,
                        originalCancellationToken);
                return await Task.Run(action, cancellationTokenWithTimeout);
            }
            catch (OperationCanceledException)
            {
                if (originalCancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                throw new RedisOperationTimeoutException();
            }
        }
    }
}