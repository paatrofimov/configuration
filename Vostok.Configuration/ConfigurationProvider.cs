using System;
using System.Collections.Concurrent;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Vostok.Configuration.Binders;
using Vostok.Configuration.Extensions;
using Vostok.Configuration.Sources;

namespace Vostok.Configuration
{
    public class ConfigurationProvider : IConfigurationProvider
    {
        private static readonly string UnknownTypeExceptionMsg = $"{nameof(IConfigurationSource)} for specified type \"typeName\" is absent. User {nameof(SetupSourceFor)} to add source.";
        private const int MaxTypeCacheSize = 10;
        private const int MaxSourceCacheSize = 10;
        private readonly ConfigurationProviderSettings settings;

        private readonly ConcurrentDictionary<Type, object> typeCache;
        private readonly ConcurrentQueue<Type> typeCacheQueue;
        private readonly ConcurrentDictionary<Type, IConfigurationSource> typeSources;
        private readonly ConcurrentDictionary<Type, IObservable<object>> typeWatchers;
        private readonly TaskSource taskSource;

        private readonly ConcurrentDictionary<IConfigurationSource, object> sourceCache;
        private readonly ConcurrentQueue<IConfigurationSource> sourceCacheQueue;

        /// <summary>
        /// Creates a <see cref="ConfigurationProvider"/> instance with given settings <paramref name="configurationProviderSettings"/>
        /// </summary>
        /// <param name="configurationProviderSettings">Provider settings</param>
        public ConfigurationProvider(ConfigurationProviderSettings configurationProviderSettings = null)
        {
            settings = configurationProviderSettings ?? new ConfigurationProviderSettings { Binder = new DefaultSettingsBinder(), ThrowExceptions = true };
            if (settings.Binder == null)
                settings.Binder = new DefaultSettingsBinder();

            typeSources = new ConcurrentDictionary<Type, IConfigurationSource>();
            typeWatchers = new ConcurrentDictionary<Type, IObservable<object>>();
            typeCache = new ConcurrentDictionary<Type, object>();
            typeCacheQueue = new ConcurrentQueue<Type>();
            sourceCache = new ConcurrentDictionary<IConfigurationSource, object>();
            sourceCacheQueue = new ConcurrentQueue<IConfigurationSource>();
            taskSource = new TaskSource();
        }

        /// <inheritdoc />
        /// <summary>
        /// <para>Returns value of given type <typeparamref name="TSettings"/> using binder from constructor.</para>
        /// <para>Uses cache.</para>
        /// </summary>
        public TSettings Get<TSettings>()
        {
            var type = typeof(TSettings);
            if (typeCache.TryGetValue(type, out var item))
                return (TSettings)item;
            if (!typeSources.ContainsKey(type))
                throw new ArgumentException($"{UnknownTypeExceptionMsg.Replace("typeName", type.Name)}");
            return taskSource.Get(Observe<TSettings>());
        }

        /// <inheritdoc />
        /// <summary>
        /// Returns value of given type <typeparamref name="TSettings"/> from specified <paramref name="source"/>.
        /// </summary>
        public TSettings Get<TSettings>(IConfigurationSource source) =>
            sourceCache.TryGetValue(source, out var item)
                ? (TSettings)item
                : taskSource.Get(Observe<TSettings>(source));

        /// <inheritdoc />
        /// <summary>
        /// <para>Subscribtion to see changes in source.</para>
        /// <para>Returns current value immediately on subscribtion.</para>
        /// </summary>
        /// <returns>Event with new value</returns>
        public IObservable<TSettings> Observe<TSettings>() =>
            Observable.Create<TSettings>(
                observer =>
                {
                    var type = typeof(TSettings);
                    if (!typeWatchers.ContainsKey(type) && typeSources.ContainsKey(type))
                        typeWatchers[type] = typeSources[type]
                            .Observe()
                            .Select(
                                rs =>
                                {
                                    if (!typeSources.TryGetValue(type, out _))
                                        throw new ArgumentException($"{UnknownTypeExceptionMsg.Replace("typeName", type.Name)}");
                                    try
                                    {
                                        return settings.Binder.Bind<TSettings>(rs);
                                    }
                                    catch (Exception e)
                                    {
                                        if (settings.ThrowExceptions)
                                            throw;
                                        settings.OnError?.Invoke(e);
                                        return typeCache.TryGetValue(type, out var value) ? value : default;
                                    }
                                });

                    if (typeWatchers.TryGetValue(type, out var watcher))
                        return watcher.Select(value =>
                            {
                                try
                                {
                                    return (TSettings)value;
                                }
                                catch (Exception e)
                                {
                                    if (settings.ThrowExceptions)
                                        throw;
                                    settings.OnError?.Invoke(e);
                                    return typeCache.TryGetValue(type, out var val) ? (TSettings)val: default;
                                }
                            })
                            .Subscribe(
                                value =>
                                {
                                    if (!typeCache.ContainsKey(type))
                                        typeCacheQueue.Enqueue(type);
                                    typeCache.AddOrUpdate(type, value, (t, o) => value);
                                    if (typeCache.Count > MaxTypeCacheSize && typeCacheQueue.TryDequeue(out var tp))
                                        typeCache.TryRemove(tp, out _);
                                    observer.OnNext(value);
                                },
                                observer.OnError);

                    return Disposable.Empty;
                });

        /// <inheritdoc />
        /// <summary>
        /// <para>Subscribtion to see changes in specified <paramref name="source"/>.</para>
        /// <para>Returns current value immediately on subscribtion.</para>
        /// </summary>
        /// <returns>Event with new value</returns>
        public IObservable<TSettings> Observe<TSettings>(IConfigurationSource source) =>
            source.Observe()
                .Select(
                    s =>
                    {
                        try
                        {
                            var value = settings.Binder.Bind<TSettings>(s);
                            if (!sourceCache.ContainsKey(source))
                                sourceCacheQueue.Enqueue(source);
                            sourceCache.AddOrUpdate(source, value, (t, o) => value);
                            if (sourceCache.Count > MaxSourceCacheSize && sourceCacheQueue.TryDequeue(out var src))
                                sourceCache.TryRemove(src, out _);
                            return value;
                        }
                        catch (Exception e)
                        {
                            if (settings.ThrowExceptions)
                                throw;
                            settings.OnError?.Invoke(e);
                            return sourceCache.TryGetValue(source, out var value)
                                ? (TSettings)value
                                : default;
                        }
                    });

        /// <summary>
        /// Changes source to combination of source for given type <typeparamref name="TSettings"/> and <paramref name="source"/>
        /// </summary>
        /// <typeparam name="TSettings">Type of souce to combine with</typeparam>
        /// <param name="source">Second souce to combine with</param>
        public ConfigurationProvider SetupSourceFor<TSettings>(IConfigurationSource source)
        {
            var type = typeof(TSettings);
            if (typeWatchers.ContainsKey(type))
                throw new InvalidOperationException($"{nameof(ConfigurationProvider)}: it is not allowed to add sources for \"{type.Name}\" to a {nameof(ConfigurationProvider)} after {nameof(Get)}() or {nameof(Observe)}() was called for this type.");

            if (typeSources.TryGetValue(type, out var existingSource))
                source = existingSource.Combine(source);
            typeSources[type] = source;

            return this;
        }
    }
}