﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reactive.Subjects;
using System.Threading;

namespace System.Reactive
{
    public class TypeOccurenceStatistics : IPlaybackConfiguration
    {
        private readonly List<TypeOccurenceAggregator> _aggregators;
        private readonly Type[] _availableTypes;
        private readonly List<IInputStream> _inputs;
        private Dictionary<Type, long> _statistics;

        public TypeOccurenceStatistics(Type[] availableTypes)
        {
            _availableTypes = availableTypes;
            _aggregators = new List<TypeOccurenceAggregator>();
            _inputs = new List<IInputStream>();
        }

        public Dictionary<Type, long> Statistics
        {
            get { return _statistics; }
        }

        public void AddInput<TInput>(
            Expression<Func<IObservable<TInput>>> createInput,
            params Type[] typeMaps)
        {
            var subject = new Subject<TInput>();

            foreach (Type mapType in typeMaps)
            {
                object mapInstance = Activator.CreateInstance(mapType);
                Type mapInterface = mapType.GetInterface(typeof (IPartitionableTypeMap<,>).Name);
                if (mapInterface == null)
                    continue;
                Type aggregatorType =
                    typeof (TypeOccurenceAggregator<,>).MakeGenericType(mapInterface.GetGenericArguments());
                object aggregatorInstance = Activator.CreateInstance(aggregatorType, mapInstance, _availableTypes);
                _aggregators.Add((TypeOccurenceAggregator) aggregatorInstance);

                subject.Subscribe((IObserver<TInput>) aggregatorInstance);
            }

            _inputs.Add(new InputStream<TInput>(createInput, subject));
        }

        public void Run()
        {
            foreach (IInputStream i in _inputs)
            {
                i.Start();
            }

            WaitHandle[] handles = (from a in _aggregators select a.Completed).ToArray();
            foreach (WaitHandle h in handles)
                h.WaitOne();

            // Merging collections that are usually small, so no worries about performance
            _statistics = new Dictionary<Type, long>();
            foreach (TypeOccurenceAggregator a in _aggregators)
            {
                if (a.Exception != null)
                    throw a.Exception;

                foreach (var pair in a.OccurenceStatistics)
                {
                    if (_statistics.ContainsKey(pair.Key))
                        _statistics[pair.Key] += pair.Value;
                    else
                        _statistics.Add(pair.Key, pair.Value);
                }
            }
        }

        private interface IInputStream
        {
            void Start();
        }

        private class InputStream<TInput> : IInputStream
        {
            private readonly IObservable<TInput> _input;
            private readonly IObserver<TInput> _observer;

            public InputStream(
                Expression<Func<IObservable<TInput>>> createInput,
                IObserver<TInput> observer)
            {
                _input = createInput.Compile()();
                _observer = observer;
            }

            public void Start()
            {
                _input.Subscribe(_observer);
            }
        }
    }
}