﻿using MySql.Data.MySqlClient;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace MySql.Server
{
    /// <summary>
    /// A singleton class controlling test database initializing and cleanup
    /// </summary>
    public class MySqlServer
    {
        private string _mysqlDirectory;
        private string _dataDirectory;
        private string _dataRootDirectory;
        private string _runningInstancesFile;
        private const string _MYSQLEXE = "mysqld_test.exe";

        private int _serverPort = 3306;
        private Process _process;

        private MySqlConnection _testConnection;
        
        public int ServerPort { get { return _serverPort; } }
        public int ProcessId
        {
            get
            {
                if (!_process.HasExited)
                {
                    return _process.Id;
                }
               
                return -1;
            }
        }



        //The Instance is running the private constructor. This way, the class is implemented as a singleton
        private static MySqlServer instance;
        public static MySqlServer Instance
        {
            get
            {
                if (instance == null)
                {
                    instance = new MySqlServer();
                }

                return instance;
            }
        }

        /// <summary>
        /// The MySQL server is started in the constructor
        /// </summary>
        private MySqlServer()
        {
            _mysqlDirectory = BaseDirHelper.GetBaseDir() + "\\tempServer";
            _dataRootDirectory = _mysqlDirectory + "\\data";
            _dataDirectory = string.Format("{0}\\{1}", _dataRootDirectory, Guid.NewGuid());
            _runningInstancesFile = BaseDirHelper.GetBaseDir() + "\\running_instances";
        }

        ~MySqlServer()
        {
            if (instance != null) { 
                instance.ShutDown();
            }

            if (_process != null)
            {
                try { 
                    _process.Kill();
                    _process.WaitForExit();
                }
                catch(Exception e)
                {
                    System.Console.WriteLine("Could not kill process while disposing");
                }

                _process.Dispose();
                _process = null;
            }

            instance = null;
        }

        public TimeSpan RemoveDirsTimeout { get; set; } = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Get a connection string for the server (no database selected)
        /// </summary>
        /// <returns>A connection string for the server</returns>
        public string GetConnectionString()
        {
            return string.Format("Server=localhost;Port={0};Protocol=pipe;SslMode=none;", _serverPort.ToString());
        }

        /// <summary>
        /// Get a connection string for the server and a specified database
        /// </summary>
        /// <param name="databaseName">The name of the database</param>
        /// <returns>A connection string for the server and database</returns>
        public string GetConnectionString(string databaseName)
        {
            return string.Format("Server=localhost;Port={0};Protocol=pipe;SslMode=none;Database={1};", _serverPort.ToString(), databaseName);
        }

        /// <summary>
        /// Create directories necessary for MySQL to run
        /// </summary>
        private void createDirs()
        {
            string[] dirs = { _mysqlDirectory, _dataRootDirectory, _dataDirectory };

            foreach (string dir in dirs) {
                DirectoryInfo checkDir = new DirectoryInfo(dir);
                try
                {
                    if (checkDir.Exists)
                        checkDir.Delete(true);

                    checkDir.Create();
                }
                catch(Exception)
                {
                    System.Console.WriteLine("Could not create or delete directory: " + checkDir.FullName);
                }
            }
        }

        /// <summary>
        /// Removes all directories related to the MySQL process
        /// </summary>
        private void removeDirs()
        {
            string[] dirs = { this._mysqlDirectory, this._dataRootDirectory, this._dataDirectory };

            foreach (string dir in dirs)
            {
                var startTime = DateTime.Now;
                DirectoryInfo checkDir = new DirectoryInfo(dir);

                if (checkDir.Exists)
                {
                    int numTry = 0;
                    while(true)
                    {
                        try { 
                            checkDir.Delete(true);
                            break;
                        }
                        catch (Exception e)
                        {
                            System.Console.WriteLine("Could not delete directory: " + checkDir.FullName + e.Message);
                            numTry++;

                            if (DateTime.Now - startTime > RemoveDirsTimeout)
                                throw new Exception(string.Format("Removing directory '{0}' failed after {1} timeout!", dir, RemoveDirsTimeout), e);
                            Thread.Sleep(50);
                        }
                    }
                }                        
            }

            try { 
                File.Delete(this._runningInstancesFile);
            }
            catch(Exception e)
            {
                throw new Exception("Could not delete runningInstancesFile", e);
            }
        }

        /// <summary>
        /// Extracts the files necessary for running MySQL as a process
        /// </summary>
        private void extractMySqlFiles()
        {
            try { 
                if (!new FileInfo(_mysqlDirectory + "\\" + _MYSQLEXE).Exists) {
                    //Extracting the two MySql files needed for the standalone server
                    File.WriteAllBytes(_mysqlDirectory + "\\" + _MYSQLEXE, Properties.Resources.mysqld);
                    File.WriteAllBytes(_mysqlDirectory + "\\errmsg.sys", Properties.Resources.errmsg);
                }
            }
            catch
            {
                throw;    
            }
        }
    
        /// <summary>
        /// Starts the server and creates all files and folders necessary
        /// </summary>
        public void StartServer()
        {
            //The process is still running, don't create a new
            if (_process != null && !_process.HasExited)
                return;

            //Cleaning up any precedented processes
            this.KillPreviousProcesses();

            createDirs();
            extractMySqlFiles();

            this._process = new Process();

            var arguments = new[]
            {
                "--standalone",
                "--console",
                string.Format("--basedir=\"{0}\"",_mysqlDirectory),
                string.Format("--lc-messages-dir=\"{0}\"",_mysqlDirectory),
                string.Format("--datadir=\"{0}\"",_dataDirectory),
                "--skip-grant-tables",
                "--enable-named-pipe",
                string.Format("--port={0}", _serverPort.ToString()),
               // "--skip-networking",
                "--innodb_fast_shutdown=2",
                "--innodb_doublewrite=OFF",
                "--innodb_log_file_size=1048576",
                "--innodb_data_file_path=ibdata1:10M;ibdata2:10M:autoextend"
            };

            _process.StartInfo.FileName = string.Format("\"{0}\\{1}\"", _mysqlDirectory, _MYSQLEXE);
            _process.StartInfo.Arguments = string.Join(" ", arguments);
            _process.StartInfo.UseShellExecute = false;
            _process.StartInfo.CreateNoWindow = true;

            System.Console.WriteLine("Running " + _process.StartInfo.FileName + " " + String.Join(" ", arguments));

            try {
                if (!_process.Start())
                    throw new Exception("Process start returned false!");
                File.WriteAllText(_runningInstancesFile, _process.Id.ToString());
            }
            catch(Exception e){
                throw new Exception("Could not start server process: " + e.Message, e);
            }

            this.waitForStartup();
        }

        /// <summary>
        /// Start the server on a specified port number
        /// </summary>
        /// <param name="serverPort">The port on which the server should listen</param>
        public void StartServer(int serverPort)
        {
            Trace.Write("STARTING\n");

            _serverPort = serverPort;
            StartServer();
        }

        /// <summary>
        /// Checks if the server is started. The most reliable way is simply to check if we can connect to it
        /// </summary>
        ///
        private void waitForStartup()
        {
            Trace.Write("waitForStartup - 1\n");
            int totalWaitTime = 0;
            int sleepTime = 100;

            Exception lastException = new Exception();

            if(_testConnection == null)
            {
                _testConnection = new MySqlConnection(GetConnectionString());
            }
            
            while (!_testConnection.State.Equals(System.Data.ConnectionState.Open))
            {
                Trace.Write("waitForStartup - 2\n");
                if (totalWaitTime > 10000)
                    throw new Exception("Server could not be started." + lastException.Message);

                totalWaitTime = totalWaitTime + sleepTime;

                try {
                    _testConnection.Open();
                }
                catch(Exception e)
                {
                    _testConnection.Close();
                    lastException = e;
                    Thread.Sleep(sleepTime);
                }
            }
            
            System.Console.WriteLine("Database connection established after " + totalWaitTime.ToString() + " miliseconds");
            _testConnection.ClearAllPoolsAsync();
            _testConnection.Close();
            _testConnection.Dispose();
            _testConnection = null;
            Trace.Write("waitForStartup - 3\n");

        }

        public void KillPreviousProcesses()
        {
            Trace.Write("KillPreviousProcesses - 1\n");

            var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(_MYSQLEXE));
            foreach (var p in processes)
            {
                p.Kill();
                p.WaitForExit();
            }

            if (!File.Exists(_runningInstancesFile))
                return;

            string[] runningInstancesIds = File.ReadAllLines(_runningInstancesFile);

            for (int i = 0; i < runningInstancesIds.Length; i++)
            {
                Trace.Write("KillPreviousProcesses - 2\n");
                try
                {
                    Process p = Process.GetProcessById(Int32.Parse(runningInstancesIds[i]));
                    p.Kill();
                    p.WaitForExit();
                }
                catch (Exception e)
                {
                    System.Console.WriteLine("Could not kill process: " + e.Message);
                }
            }

            try { 
                File.Delete(_runningInstancesFile);
            }
            catch(Exception e)
            {
                System.Console.WriteLine("Could not delete running instances file");
            }

            Trace.Write("KillPreviousProcesses - 3\n");

            Trace.Write("KillPreviousProcesses - 5\n");

            this.removeDirs();
        }

        /// <summary>
        /// Shuts down the server and removes all files related to it
        /// </summary>
        public void ShutDown()
        {
            Trace.Write("ShutDown - 1\n");
            if (this._testConnection != null && this._testConnection.State != System.Data.ConnectionState.Closed)
                this._testConnection.Close();

            try
            {
                if (this._process != null)
                {
                    Trace.Write("ShutDown - 2\n");
                    if (!this._process.HasExited)
                    {
                        Trace.Write("ShutDown - 3\n");
                        this._process.Kill();
                        _process.WaitForExit();
                    }
                    //System.Console.WriteLine("Process killed");
                    this._process = null;
                }

                //System.Console.WriteLine("Process killed");
            }
            catch (Exception e)
            {
                System.Console.WriteLine("Could not close database server process: " + e.Message);
                throw;
            }

            removeDirs();
        }
    }
}
