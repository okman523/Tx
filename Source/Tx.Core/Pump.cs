﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Threading;

namespace System.Reactive
{
    // BUGBUG: The Pump seems something that should exist in Rx
    // Exposing this here as public is weird
    public abstract class Pump
    {
        protected ManualResetEvent _completed = new ManualResetEvent(false);

        public WaitHandle Completed
        {
            get { return _completed; }
        }
    }

    internal class OutputPump<T> : Pump, IDisposable
    {
        private readonly IEnumerator<T> _source;
        private readonly IObserver<T> _target;
        private readonly Thread _thread;
        private readonly WaitHandle _waitStart;
        private long _eventsRead;

        public OutputPump(IEnumerable<T> source, IObserver<T> target, WaitHandle waitStart)
        {
            _source = source.GetEnumerator();
            _target = target;
            _waitStart = waitStart;
            _thread = new Thread(ThreadProc) {Name = "Pump " + typeof (T).Name};
            _thread.Start();
        }

        public void Dispose()
        {
            _waitStart.Dispose();
            _completed.Dispose();
        }

        private void ThreadProc()
        {
            _waitStart.WaitOne();
            while (true)
            {
                try
                {
                    if (!_source.MoveNext())
                        break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    try
                    {
                        _target.OnError(ex);
                    }
                    catch
                    {
                    }

                    break;
                }

                _eventsRead++;

                try
                {
                    _target.OnNext(_source.Current);
                }
                catch (Exception ex)
                {
                    _target.OnError(ex);
                }
            }

            _target.OnCompleted();
            _completed.Set();
        }
    }
}