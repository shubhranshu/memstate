using System.Collections.Generic;
using Memstate.Models.KeyValue;
using NUnit.Framework;

namespace Memstate.Test.Models
{
    using System;
    using Memstate.Models;

    public class KeyValueStoreProxyTests
    {
        private IKeyValueStore<int> _keyValueStore;

        [SetUp]
        public void Setup()
        {
            MemstateSettings config = new MemstateSettings();
            config.FileSystem = new InMemoryFileSystem();
            IKeyValueStore<int> model = new KeyValueStore<int>();
            var engine = new EngineBuilder(config).Build(model);
            var client = new LocalClient<IKeyValueStore<int>>(engine);
            _keyValueStore = client.GetDispatchProxy();
        }

        [Test]
        public void Set_stores_value()
        {
            _keyValueStore.Set("KEY", 1);
            var node = _keyValueStore.Get("KEY");
            Assert.AreEqual(1, node.Value);
        }

        [Test]
        public void Set_new_key_yields_correct_version()
        {
            _keyValueStore.Set("KEY", 1);
            var node = _keyValueStore.Get("KEY");
            Assert.AreEqual(1, node.Version);
        }

        [Test]
        public void Update_bumps_version()
        {
            _keyValueStore.Set("KEY", 1);
            _keyValueStore.Set("KEY", 2);
            var node = _keyValueStore.Get("KEY");
            Assert.AreEqual(2, node.Version);
        }

        [Test]
        public void Remove_throws_when_key_not_exists()
        {
            Assert.Throws<AggregateException> (() =>
            {
                _keyValueStore.Remove("KEY");
            });
        }
    }
}