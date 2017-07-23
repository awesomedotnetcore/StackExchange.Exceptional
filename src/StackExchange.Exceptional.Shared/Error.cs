using Newtonsoft.Json;
using StackExchange.Exceptional.Internal;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace StackExchange.Exceptional
{
    /// <summary>
    /// Represents a logical application error (as opposed to the actual <see cref="Exception"/> it may be representing).
    /// </summary>
    [Serializable]
    public class Error
    {
        private ExceptionalSettings Settings => ExceptionalSettings.Current;

        /// <summary>
        /// Event handler to run before an exception is logged to the store.
        /// </summary>
        public static event EventHandler<ErrorBeforeLogEventArgs> OnBeforeLog;

        /// <summary>
        /// Event handler to run after an exception has been logged to the store.
        /// </summary>
        public static event EventHandler<ErrorAfterLogEventArgs> OnAfterLog;

        /// <summary>
        /// The ID on this error, strictly for primary keying on persistent stores.
        /// </summary>
        [JsonIgnore]
        public long Id { get; set; }

        /// <summary>
        /// Unique identifier for this error, generated on the server it came from.
        /// </summary>
        public Guid GUID { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="Error"/> class.
        /// </summary>
        public Error() { }

        /// <summary>
        /// Initializes a new instance of the <see cref="Error"/> class from a given <see cref="Exception"/> instance.
        /// </summary>
        /// <param name="e">The exception we intend to log.</param>
        /// <param name="applicationName">The application name to log as (used for overriding current settings).</param>
        public Error(Exception e, string applicationName = null)
        {
            Exception = e ?? throw new ArgumentNullException(nameof(e));
            var baseException = e;

            // If it's not a .NET framework exception, usually more information is being added,
            // so use the wrapper for the message, type, etc.
            // If it's a .NET framework exception type, drill down and get the innermost exception.
            if (IsBuiltInException(e))
                baseException = e.GetBaseException();

            GUID = Guid.NewGuid();
            ApplicationName = applicationName ?? Settings.ApplicationName;
            MachineName = Environment.MachineName;
            Type = baseException.GetType().FullName;
            Message = baseException.Message;
            Source = baseException.Source;
            Detail = e.ToString();
            CreationDate = DateTime.UtcNow;
            DuplicateCount = 1;

            ErrorHash = GetHash();

            var exCursor = e;
            while (exCursor != null)
            {
                AddFormData(exCursor);
                exCursor = exCursor.InnerException;
            }
        }

        internal void AddFormData(Exception exception)
        {
            // Regardless of what Resharper may be telling you, .Data can be null on things like a null ref exception.
            if (exception.Data == null) return;

            // Historical special case
            if (exception.Data.Contains("SQL"))
                SQL = exception.Data["SQL"] as string;

            if (exception is SqlException se)
            {
                if (CustomData == null)
                    CustomData = new Dictionary<string, string>();

                CustomData["SQL-Server"] = se.Server;
                CustomData["SQL-ErrorNumber"] = se.Number.ToString();
                CustomData["SQL-LineNumber"] = se.LineNumber.ToString();
                if (se.Procedure.HasValue())
                {
                    CustomData["SQL-Procedure"] = se.Procedure;
                }
            }

            if (Settings.DataIncludeRegex != null)
            {
                if (CustomData == null)
                    CustomData = new Dictionary<string, string>();

                foreach (string k in exception.Data.Keys)
                {
                    if (!Settings.DataIncludeRegex.IsMatch(k)) continue;
                    CustomData[k] = exception.Data[k] != null ? exception.Data[k].ToString() : "";
                }
            }
        }

        /// <summary>
        /// Logs this error to a specific store.
        /// </summary>
        /// <param name="store">The store to log to.</param>
        /// <returns>The error if logged, or null if logging was aborted.</returns>
        public bool LogToStore(ErrorStore store)
        {
            if (OnBeforeLog != null)
            {
                try
                {
                    var args = new ErrorBeforeLogEventArgs(this);
                    OnBeforeLog(store, args);
                    if (args.Abort) return false; // if we've been told to abort, then abort dammit!
                }
                catch (Exception e)
                {
                    Trace.WriteLine(e);
                }
            }

            Trace.WriteLine(Exception); // always echo the error to trace for local debugging
            store.Log(this);

            if (OnAfterLog != null)
            {
                try
                {
                    OnAfterLog(store, new ErrorAfterLogEventArgs(this));
                }
                catch (Exception e)
                {
                    Trace.WriteLine(e);
                }
            }

            return true;
        }

        /// <summary>
        /// Returns if the type of the exception is built into the .NET framework.
        /// </summary>
        /// <param name="e">The exception to check.</param>
        /// <returns>True if the exception is a type from within the CLR, false if it's a user/third party type.</returns>
        private bool IsBuiltInException(Exception e) => e.GetType().Module.ScopeName == "CommonLanguageRuntimeLibrary";

        /// <summary>
        /// Gets a unique-enough hash of this error. Stored as a quick comparison mechanism to roll-up duplicate errors.
        /// </summary>
        /// <returns>A "Unique" hash for this error.</returns>
        public int? GetHash()
        {
            if (!Detail.HasValue()) return null;

            var result = Detail.GetHashCode();
            if (RollupPerServer && MachineName.HasValue())
                result = (result * 397)^ MachineName.GetHashCode();

            return result;
        }

        /// <summary>
        /// Whether this error is protected from deletion.
        /// </summary>
        public bool IsProtected { get; set; }

        /// <summary>
        /// For notifier usage - whether this error is a duplicate (already seen recently).
        /// Recent is defined by the <see cref="ErrorStoreSettings.RollupPeriod"/> setting.
        /// </summary>
        [JsonIgnore]
        public bool IsDuplicate { get; set; }

        /// <summary>
        /// The <see cref="Exception"/> instance used to create this error.
        /// </summary>
        [JsonIgnore]
        public Exception Exception { get; set; }

        /// <summary>
        /// The name of the application that threw this exception.
        /// </summary>
        public string ApplicationName { get; set; }

        /// <summary>
        /// The hostname where the exception occurred.
        /// </summary>
        public string MachineName { get; set; }

        /// <summary>
        /// The type error.
        /// </summary>
        public string Type { get; set; }

        /// <summary>
        /// The source of this error.
        /// </summary>
        public string Source { get; set; }

        /// <summary>
        /// Exception message.
        /// </summary>
        public string Message { get; set; }

        /// <summary>
        /// The detail/stack trace of this error.
        /// </summary>
        public string Detail { get; set; }

        /// <summary>
        /// The hash that describes this error.
        /// </summary>
        public int? ErrorHash { get; set; }

        /// <summary>
        /// The time in UTC that the error originally occurred.
        /// </summary>
        public DateTime CreationDate { get; set; }

        /// <summary>
        /// The time in UTC that the error last occurred.
        /// </summary>
        public DateTime? LastLogDate { get; set; }

        /// <summary>
        /// The HTTP Status code associated with the request.
        /// </summary>
        public int? StatusCode { get; set; }

        /// <summary>
        /// The server variables collection for the request.
        /// </summary>
        [JsonIgnore]
        public NameValueCollection ServerVariables { get; set; }

        /// <summary>
        /// The query string collection for the request.
        /// </summary>
        [JsonIgnore]
        public NameValueCollection QueryString { get; set; }

        /// <summary>
        /// The form collection for the request.
        /// </summary>
        [JsonIgnore]
        public NameValueCollection Form { get; set; }

        /// <summary>
        /// A collection representing the client cookies of the request.
        /// </summary>
        [JsonIgnore]
        public NameValueCollection Cookies { get; set; }

        /// <summary>
        /// A collection representing the headers sent with the request.
        /// </summary>
        [JsonIgnore]
        public NameValueCollection RequestHeaders { get; set; }

        /// <summary>
        /// A collection of custom data added at log time.
        /// </summary>
        public Dictionary<string, string> CustomData { get; set; }

        /// <summary>
        /// The number of newer Errors that have been discarded because they match this Error and fall 
        /// within the configured <see cref="ErrorStoreSettings.RollupPeriod"/> <see cref="TimeSpan"/> value.
        /// </summary>
        public int? DuplicateCount { get; set; }

        /// <summary>
        /// Gets the SQL command text associated with this error.
        /// </summary>
        public string SQL { get; set; }

        /// <summary>
        /// Date this error was deleted (for stores that support deletion and retention, e.g. SQL)
        /// </summary>
        public DateTime? DeletionDate { get; set; }

        private string _host;
        /// <summary>
        /// The URL host of the request causing this error.
        /// </summary>
        public string Host
        {
            get => _host ?? (_host = ServerVariables?["HTTP_HOST"] ?? "");
            set => _host = value;
        }

        private string _url;
        /// <summary>
        /// The URL path of the request causing this error.
        /// </summary>
        public string Url
        {
            get => _url ?? (_url = ServerVariables?["URL"] ?? "");
            set => _url = value;
        }

        private string _httpMethod;
        /// <summary>
        /// The HTTP Method causing this error, e.g. GET or POST.
        /// </summary>
        public string HTTPMethod
        {
            get => _httpMethod ?? (_httpMethod = ServerVariables?["REQUEST_METHOD"] ?? "");
            set => _httpMethod = value;
        }

        private string _ipAddress;
        /// <summary>
        /// The IPAddress of the request causing this error.
        /// </summary>
        public string IPAddress
        {
            get => _ipAddress ?? (_ipAddress = ServerVariables?.GetRemoteIP() ?? "");
            set => _ipAddress = value;
        }

        /// <summary>
        /// Json populated from database stored, deserialized after if needed.
        /// </summary>
        [JsonIgnore]
        public string FullJson { get; set; }

        /// <summary>
        /// Whether to roll up errors per-server. E.g. should an identical error happening on 2 separate servers 
        /// be a <see cref="DuplicateCount"/>++, or 2 separate errors.
        /// </summary>
        [JsonIgnore]
        public bool RollupPerServer { get; set; }

        /// <summary>
        /// Returns the value of the <see cref="Message"/> property.
        /// </summary>
        public override string ToString() => Message;

        /// <summary>
        /// Create a copy of the error and collections so if it's modified in memory logging is not affected.
        /// </summary>
        /// <returns>A clone of this error.</returns>
        public Error Clone()
        {
            var copy = (Error) MemberwiseClone();
            if (ServerVariables != null) copy.ServerVariables = new NameValueCollection(ServerVariables);
            if (QueryString != null) copy.QueryString = new NameValueCollection(QueryString);
            if (Form != null) copy.Form = new NameValueCollection(Form);
            if (Cookies != null) copy.Cookies = new NameValueCollection(Cookies);
            if (RequestHeaders != null) copy.RequestHeaders = new NameValueCollection(RequestHeaders);
            if (CustomData != null) copy.CustomData = new Dictionary<string, string>(CustomData);
            return copy;
        }

        /// <summary>
        /// Variables strictly for JSON serialization, to maintain non-dictionary behavior.
        /// </summary>
        public List<NameValuePair> ServerVariablesSerializable
        {
            get => GetPairs(ServerVariables);
            set => ServerVariables = GetNameValueCollection(value);
        }
        /// <summary>
        /// Variables strictly for JSON serialization, to maintain non-dictionary behavior.
        /// </summary>
        public List<NameValuePair> QueryStringSerializable
        {
            get => GetPairs(QueryString);
            set => QueryString = GetNameValueCollection(value);
        }
        /// <summary>
        /// Variables strictly for JSON serialization, to maintain non-dictionary behavior.
        /// </summary>
        public List<NameValuePair> FormSerializable
        {
            get => GetPairs(Form);
            set => Form = GetNameValueCollection(value);
        }
        /// <summary>
        /// Variables strictly for JSON serialization, to maintain non-dictionary behavior.
        /// </summary>
        public List<NameValuePair> CookiesSerializable
        {
            get => GetPairs(Cookies);
            set => Cookies = GetNameValueCollection(value);
        }
        /// <summary>
        /// Variables strictly for JSON serialization, to maintain non-dictionary behavior.
        /// </summary>
        public List<NameValuePair> RequestHeadersSerializable
        {
            get => GetPairs(RequestHeaders);
            set => RequestHeaders = GetNameValueCollection(value);
        }

        /// <summary>
        /// Gets a JSON representation for this error.
        /// </summary>
        public string ToJson() => JsonConvert.SerializeObject(this);

        /// <summary>
        /// Gets a JSON representation for this error suitable for cross-domain.
        /// </summary>
        /// <param name="sb">The <see cref="StringBuilder"/> to write to.</param>
        public void WriteDetailedJson(StringBuilder sb)
        {
            using (var sw = new StringWriter(sb))
            using (var w = new JsonTextWriter(sw))
            {
                w.StringEscapeHandling = StringEscapeHandling.EscapeHtml;
                JsonTextWriter WriteName(string name)
                {
                    w.WritePropertyName(name);
                    return w;
                }
                JsonTextWriter WritePairs(string name, List<NameValuePair> pairs)
                {
                    WriteName(name).WriteStartObject();
                    if (pairs != null)
                    {
                        foreach (var p in pairs)
                        {
                            WriteName(p.Name).WriteValue(p.Value);
                        }
                    }
                    w.WriteEndObject();
                    return w;
                }
                JsonTextWriter WriteDictionary(string name, Dictionary<string, string> pairs)
                {
                    WriteName(name).WriteStartObject();
                    if (pairs != null)
                    {
                        foreach (var p in pairs)
                        {
                            WriteName(p.Key).WriteValue(p.Value);
                        }
                    }
                    w.WriteEndObject();
                    return w;
                }
                w.WriteStartObject();
                WriteName(nameof(GUID)).WriteValue(GUID);
                WriteName(nameof(ApplicationName)).WriteValue(ApplicationName);
                WriteName(nameof(CreationDate)).WriteValue(CreationDate.ToEpochTime());
                WriteName(nameof(DeletionDate)).WriteValue(DeletionDate.ToEpochTime());
                WriteName(nameof(Detail)).WriteValue(Detail);
                WriteName(nameof(DuplicateCount)).WriteValue(DuplicateCount);
                WriteName(nameof(ErrorHash)).WriteValue(ErrorHash);
                WriteName(nameof(HTTPMethod)).WriteValue(HTTPMethod);
                WriteName(nameof(Host)).WriteValue(Host);
                WriteName(nameof(IPAddress)).WriteValue(IPAddress);
                WriteName(nameof(IsProtected)).WriteValue(IsProtected);
                WriteName(nameof(MachineName)).WriteValue(MachineName);
                WriteName(nameof(Message)).WriteValue(Message);
                WriteName(nameof(SQL)).WriteValue(SQL);
                WriteName(nameof(Source)).WriteValue(Source);
                WriteName(nameof(StatusCode)).WriteValue(StatusCode);
                WriteName(nameof(Type)).WriteValue(Type);
                WriteName(nameof(Url)).WriteValue(Url);
                WriteName(nameof(QueryString)).WriteValue(ServerVariables?["QUERY_STRING"]);
                WriteDictionary(nameof(CustomData), CustomData);
                WritePairs(nameof(ServerVariables), ServerVariablesSerializable);
                WritePairs(nameof(Cookies), CookiesSerializable);
                WritePairs(nameof(RequestHeaders), RequestHeadersSerializable);
                WritePairs(nameof(QueryString), QueryStringSerializable);
                WritePairs(nameof(Form), FormSerializable);
                w.WriteEndObject();
            }
        }

        /// <summary>
        /// Deserializes provided JSON into an Error object.
        /// </summary>
        /// <param name="json">JSON representing an Error.</param>
        /// <returns>The Error object.</returns>
        public static Error FromJson(string json) => JsonConvert.DeserializeObject<Error>(json);

        /// <summary>
        /// Serialization class in place of the NameValueCollection pairs.
        /// </summary>
        /// <remarks>This exists because things like a querystring can halve multiple values, they are not a dictionary.</remarks>
        public class NameValuePair
        {
            /// <summary>
            /// The name for this variable.
            /// </summary>
            public string Name { get; set; }
            /// <summary>
            /// The value for this variable.
            /// </summary>
            public string Value { get; set; }
        }

        private List<NameValuePair> GetPairs(NameValueCollection nvc)
        {
            var result = new List<NameValuePair>();
            if (nvc == null) return null;

            for (int i = 0; i < nvc.Count; i++)
            {
                result.Add(new NameValuePair {Name = nvc.GetKey(i), Value = nvc.Get(i)});
            }
            return result;
        }

        private NameValueCollection GetNameValueCollection(List<NameValuePair> pairs)
        {
            var result = new NameValueCollection();
            if (pairs == null) return null;

            foreach(var p in pairs)
            {
                result.Add(p.Name, p.Value);
            }
            return result;
        }
    }
}