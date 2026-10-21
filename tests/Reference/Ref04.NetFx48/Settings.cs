using System;
using System.Collections;
using System.Collections.Generic;
using System.Configuration;
using System.Runtime.Serialization;
using System.Threading;
using System.Xml;

namespace Ref.NetFx48
{
    [Serializable]
    public class SettingsException : Exception
    {
        public SettingsException(string key) : base("missing setting: " + key) => Key = key;

        protected SettingsException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
            Key = info.GetString("Key");
        }

        public string Key { get; }

        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            base.GetObjectData(info, context);
            info.AddValue("Key", Key);
        }
    }

    public sealed class AppSettings : ICloneable
    {
        private readonly Hashtable _values = new Hashtable(StringComparer.OrdinalIgnoreCase);

        public static AppSettings FromConfiguration()
        {
            var settings = new AppSettings();
            foreach (string key in ConfigurationManager.AppSettings.AllKeys)
                settings._values[key] = ConfigurationManager.AppSettings[key];
            return settings;
        }

        public static AppSettings FromXml(string xml)
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var settings = new AppSettings();
            foreach (XmlNode node in doc.SelectNodes("/settings/add"))
            {
                var key = node.Attributes["key"]?.Value;
                if (key == null) continue;
                settings._values[key] = node.Attributes["value"]?.Value ?? string.Empty;
            }
            return settings;
        }

        public string this[string key]
        {
            get
            {
                var value = _values[key] as string;
                if (value == null) throw new SettingsException(key);
                return value;
            }
            set { _values[key] = value; }
        }

        public int GetInt(string key, int fallback)
        {
            int result;
            return _values.ContainsKey(key) && int.TryParse((string)_values[key], out result) ? result : fallback;
        }

        public object Clone()
        {
            var copy = new AppSettings();
            foreach (DictionaryEntry e in _values) copy._values[e.Key] = e.Value;
            return copy;
        }
    }

    public delegate void WorkCompleted(object sender, int processed);

    public class BackgroundCounter
    {
        private int _processed;
        private readonly object _gate = new object();

        public event WorkCompleted Completed;

        public void Run(IList<string> items)
        {
            int pending = items.Count;
            using (var done = new ManualResetEvent(pending == 0))
            {
                foreach (var item in items)
                {
                    ThreadPool.QueueUserWorkItem(state =>
                    {
                        lock (_gate)
                        {
                            if (!string.IsNullOrEmpty((string)state)) _processed++;
                        }
                        if (Interlocked.Decrement(ref pending) == 0) done.Set();
                    }, item);
                }
                done.WaitOne();
            }
            OnCompleted(_processed);
        }

        protected virtual void OnCompleted(int processed)
        {
            var handler = Completed;
            if (handler != null) handler(this, processed);
        }
    }
}
