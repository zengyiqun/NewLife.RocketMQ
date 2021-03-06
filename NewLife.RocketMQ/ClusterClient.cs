﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using NewLife.Collections;
using NewLife.Log;
using NewLife.Net;
using NewLife.RocketMQ.Client;
using NewLife.RocketMQ.Protocol;
using NewLife.Serialization;

namespace NewLife.RocketMQ
{
    /// <summary>集群客户端</summary>
    /// <remarks>
    /// 维护到一个集群的客户端连接，内部采用负载均衡调度算法。
    /// </remarks>
    public abstract class ClusterClient : DisposeBase
    {
        #region 属性
        /// <summary>编号</summary>
        public String Id { get; set; }

        /// <summary>名称</summary>
        public String Name { get; set; }

        /// <summary>超时。默认3000ms</summary>
        public Int32 Timeout { get; set; } = 3_000;

        /// <summary>服务器地址集合</summary>
        public NetUri[] Servers { get; set; }

        /// <summary>配置</summary>
        public MqBase Config { get; set; }

        //private TcpClient _Client;
        //private Stream _Stream;
        #endregion

        #region 构造
        /// <summary>实例化</summary>
        public ClusterClient()
        {
            _Pool = new MyPool { Client = this };
        }

        /// <summary>销毁</summary>
        /// <param name="disposing"></param>
        protected override void OnDispose(Boolean disposing)
        {
            base.OnDispose(disposing);

            _Pool.TryDispose();
        }
        #endregion

        #region 方法
        /// <summary>开始</summary>
        public virtual void Start()
        {
            WriteLog("[{0}]集群地址：{1}", Name, Servers.Join(";", e => $"{e.Host}:{e.Port}"));
        }

        private Int32 g_id;
        /// <summary>发送命令</summary>
        /// <param name="cmd"></param>
        /// <returns></returns>
        protected virtual Command Send(Command cmd)
        {
            if (cmd.Header.Opaque == 0) cmd.Header.Opaque = g_id++;

            // 签名。阿里云ONS需要反射消息具体字段，把值转字符串后拼起来，再加上body后，取HmacSHA1
            var cfg = Config;
            if (!cfg.AccessKey.IsNullOrEmpty())
            {
                var sha = new HMACSHA1(cfg.SecretKey.GetBytes());

                var ms = new MemoryStream();
                // AccessKey + OnsChannel
                ms.Write(cfg.AccessKey.GetBytes());
                ms.Write(cfg.OnsChannel.GetBytes());
                // ExtFields
                foreach (var item in cmd.Header.ExtFields)
                {
                    if (item.Value != null) ms.Write(item.Value.GetBytes());
                }
                // Body
                if (cmd.Body != null && cmd.Body.Length > 0) ms.Write(cmd.Body);

                var sign = sha.ComputeHash(ms.ToArray());

                var dic = cmd.Header.ExtFields;

                dic["Signature"] = sign.ToBase64();
                dic["AccessKey"] = cfg.AccessKey;
                dic["OnsChannel"] = cfg.OnsChannel;
            }

            // 轮询调用
            Exception last = null;
            for (var i = 0; i < Servers.Length; i++)
            {
                var client = _Pool.Get();
                try
                {
                    var ns = client.GetStream();

                    cmd.Write(ns);

                    var rs = new Command();
                    rs.Read(ns);

                    return rs;
                }
                catch (Exception ex) { last = ex; }
                finally
                {
                    _Pool.Put(client);
                }
            }

            throw last;
        }

        /// <summary>发送指定类型的命令</summary>
        /// <param name="request"></param>
        /// <param name="body"></param>
        /// <param name="extFields"></param>
        /// <returns></returns>
        internal virtual Command Invoke(RequestCode request, Object body, Object extFields = null)
        {
            var header = new Header
            {
                Code = (Int32)request,
            };

            var cmd = new Command
            {
                Header = header,
            };

            // 主体
            if (body is Byte[] buf)
                cmd.Body = buf;
            else if (body != null)
                cmd.Body = body.ToJson().GetBytes();

            if (extFields != null)
            {
                //header.ExtFields.Merge(extFields);// = extFields.ToDictionary().ToDictionary(e => e.Key, e => e.Value + "");
                var dic = header.ExtFields;
                foreach (var item in extFields.ToDictionary())
                {
                    dic[item.Key] = item.Value + "";
                }
            }

            OnBuild(header);

            var rs = Send(cmd);

            // 判断异常响应
            if (rs.Header.Code != 0)
            {
                // 优化异常输出
                var err = rs.Header.Remark;
                var p = err.IndexOf("Exception: ");
                if (p >= 0) err = err.Substring(p + "Exception: ".Length);
                p = err.IndexOf(", ");
                if (p > 0) err = err.Substring(0, p);

                throw new ResponseException(rs.Header.Code, err);
            }

            return rs;
        }

        /// <summary>建立命令时，处理头部</summary>
        /// <param name="header"></param>
        protected virtual void OnBuild(Header header)
        {
            // 阿里云支持 CSharp
            var cfg = Config;
            if (!cfg.AccessKey.IsNullOrEmpty()) header.Language = "CSharp";

            //// 阿里云密钥
            //if (!cfg.AccessKey.IsNullOrEmpty())
            //{
            //    var dic = header.ExtFields;

            //    dic["AccessKey"] = cfg.AccessKey;
            //    dic["SecretKey"] = cfg.SecretKey;

            //    if (!cfg.OnsChannel.IsNullOrEmpty()) dic["OnsChannel"] = cfg.OnsChannel;
            //}
        }
        #endregion

        #region 连接池
        private readonly MyPool _Pool;

        class MyPool : ObjectPool<TcpClient>
        {
            public ClusterClient Client { get; set; }

            protected override TcpClient OnCreate() => Client.OnCreate();
        }

        private Int32 _ServerIndex;
        /// <summary>创建网络连接。轮询使用地址</summary>
        /// <returns></returns>
        protected virtual TcpClient OnCreate()
        {
            var idx = Interlocked.Increment(ref _ServerIndex);
            idx = (idx - 1) % Servers.Length;

            var uri = Servers[idx];
            //WriteLog("正在连接[{0}:{1}]", uri.Host, uri.Port);

            var client = new TcpClient();

            var timeout = Timeout;

            // 采用异步来解决连接超时设置问题
            var ar = client.BeginConnect(uri.Address, uri.Port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(timeout, false))
            {
                client.Close();
                throw new TimeoutException($"连接[{uri}][{timeout}ms]超时！");
            }

            return client;
        }
        #endregion

        #region 日志
        /// <summary>日志</summary>
        public ILog Log { get; set; } = Logger.Null;

        /// <summary>写日志</summary>
        /// <param name="format"></param>
        /// <param name="args"></param>
        public void WriteLog(String format, params Object[] args) => Log?.Info(format, args);
        #endregion
    }
}