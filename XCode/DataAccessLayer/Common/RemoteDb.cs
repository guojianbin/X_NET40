﻿using System;
using System.Data;
using System.Threading;

namespace XCode.DataAccessLayer
{
    /// <summary>远程数据库。一般是分为客户端服务器的中大型数据库，该类数据库支持完整的SQL92</summary>
    abstract class RemoteDb : DbBase
    {
        #region 属性
        /// <summary>系统数据库名</summary>
        public virtual String SystemDatabaseName { get { return "master"; } }

        //private String _ServerVersion;
        /// <summary>数据库服务器版本</summary>
        public override String ServerVersion
        {
            get
            {
                var ver = _ServerVersion;
                if (ver != null) return ver;
                _ServerVersion = String.Empty;

                var session = CreateSession() as RemoteDbSession;
                ver = _ServerVersion = session.ProcessWithSystem(s =>
                {
                    if (!session.Opened) session.Open();
                    try
                    {
                        return session.Conn.ServerVersion;
                    }
                    finally
                    {
                        session.AutoClose();
                    }
                }) as String;

                return ver;
            }
        }

        private String _User;
        /// <summary>用户名UserID</summary>
        public String User
        {
            get
            {
                if (_User != null) return _User;

                var connStr = ConnectionString;

                if (String.IsNullOrEmpty(connStr)) return null;

                var ocsb = Factory.CreateConnectionStringBuilder();
                ocsb.ConnectionString = connStr;

                if (ocsb.ContainsKey("User ID"))
                    _User = (String)ocsb["User ID"];
                else if (ocsb.ContainsKey("User"))
                    _User = (String)ocsb["User"];
                else if (ocsb.ContainsKey("uid"))
                    _User = (String)ocsb["uid"];
                else
                    _User = String.Empty;

                return _User;
            }
        }

        protected override String DefaultConnectionString
        {
            get
            {
                var builder = Factory.CreateConnectionStringBuilder();
                if (builder != null)
                {
                    builder["Server"] = "127.0.0.1";
                    // Oracle连接字符串不支持Database关键字
                    if (Type != DatabaseType.Oracle) builder["Database"] = SystemDatabaseName;
                    return builder.ToString();
                }

                return base.DefaultConnectionString;
            }
        }

        const String Pooling = "Pooling";
        protected override void OnSetConnectionString(ConnectionStringBuilder builder)
        {
            base.OnSetConnectionString(builder);

            // 关闭底层连接池，使用XCode连接池
            builder.TryAdd(Pooling, "false");
        }
        #endregion
    }

    /// <summary>远程数据库会话</summary>
    abstract class RemoteDbSession : DbSession
    {
        #region 属性
        /// <summary>系统数据库名</summary>
        public String SystemDatabaseName
        {
            get
            {
                //return Database is RemoteDb ? (Database as RemoteDb).SystemDatabaseName : null;
                // 减少一步类型转换
                var remotedb = Database as RemoteDb;
                return remotedb?.SystemDatabaseName;
            }
        }
        #endregion

        #region 构造函数
        public RemoteDbSession(IDatabase db) : base(db) { }
        #endregion

        #region 架构
        public override DataTable GetSchema(String collectionName, String[] restrictionValues)
        {
            try
            {
                return base.GetSchema(collectionName, restrictionValues);
            }
            catch (Exception ex)
            {
                DAL.WriteLog("[3]GetSchema({0})异常重试！{1},连接字符串 {2}", collectionName, ex.Message, ConnectionString, Database.ConnName);

                // 如果没有数据库，登录会失败，需要切换到系统数据库再试试
                return ProcessWithSystem(s => base.GetSchema(collectionName, restrictionValues)) as DataTable;
            }
        }
        #endregion

        #region 系统权限处理
        public Object ProcessWithSystem(Func<IDbSession, Object> callback)
        {
            var dbname = DatabaseName;
            var sysdbname = SystemDatabaseName;

            // 如果指定了数据库名，并且不是master，则切换到master
            if (!dbname.IsNullOrEmpty() && !dbname.EqualIgnoreCase(sysdbname))
            {
                using (var conn = Database.Factory.CreateConnection())
                {
                    conn.ConnectionString = ConnectionString;

                    OpenDatabase(conn, sysdbname);

                    return callback(this);
                }
            }
            else
            {
                return callback(this);
            }
        }

        private static void OpenDatabase(IDbConnection conn, String dbName)
        {
            // 如果没有打开，则改变链接字符串
            var builder = new ConnectionStringBuilder(conn.ConnectionString);
            var flag = false;
            if (builder["Database"] != null)
            {
                builder["Database"] = dbName;
                flag = true;
            }
            else if (builder["Initial Catalog"] != null)
            {
                builder["Initial Catalog"] = dbName;
                flag = true;
            }
            if (flag)
            {
                var connStr = builder.ToString();
                conn.ConnectionString = connStr;
            }

            conn.Open();
        }
        #endregion
    }

    /// <summary>远程数据库元数据</summary>
    abstract class RemoteDbMetaData : DbMetaData
    {
        #region 属性
        /// <summary>系统数据库名</summary>
        public String SystemDatabaseName { get { return (Database as RemoteDb)?.SystemDatabaseName; } }
        #endregion

        #region 架构定义
        public override Object SetSchema(DDLSchema schema, params Object[] values)
        {
            var session = Database.CreateSession();
            var databaseName = session.DatabaseName;

            if (values != null && values.Length > 0 && values[0] is String && values[0] + "" != "") databaseName = values[0] + "";  //ahuang 2014.06.12  类型强制转string的bug

            switch (schema)
            {
                case DDLSchema.TableExist:
                    return session.QueryCount(GetSchemaSQL(schema, values)) > 0;

                case DDLSchema.DatabaseExist:
                    return ProcessWithSystem(s => DatabaseExist(databaseName));

                case DDLSchema.CreateDatabase:
                    values = new Object[] { databaseName, values == null || values.Length < 2 ? null : values[1] };

                    var obj = ProcessWithSystem(s => base.SetSchema(schema, values));

                    //// 创建数据库后，需要等待它初始化
                    //Thread.Sleep(5000);

                    return obj;

                case DDLSchema.DropDatabase:
                    return ProcessWithSystem(s => DropDatabase(databaseName));

                default:
                    break;
            }
            return base.SetSchema(schema, values);
        }

        protected virtual Boolean DatabaseExist(String databaseName)
        {
            var session = Database.CreateSession();
            return session.QueryCount(GetSchemaSQL(DDLSchema.DatabaseExist, new Object[] { databaseName })) > 0;
        }

        protected virtual Boolean DropDatabase(String databaseName)
        {
            return (Boolean)base.SetSchema(DDLSchema.DropDatabase, new Object[] { databaseName });
        }

        Object ProcessWithSystem(Func<IDbSession, Object> callback)
        {
            return (Database.CreateSession() as RemoteDbSession).ProcessWithSystem(callback);
        }
        #endregion
    }
}
