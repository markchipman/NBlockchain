﻿
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NBlockchain.P2PPrototocol.NodeJSAPI;
using NBlockchain.P2PPrototocol.Repository;
using static NBlockchain.P2PPrototocol.Network.Message;

namespace NBlockchain.P2PPrototocol.Network
{

  /// <summary>
  /// CommunicationEngine
  /// </summary>
  internal class CommunicationEngine : IDisposable, INetworkAgentAPI
  {

    #region API
    /// <summary>
    /// Craetes instance of CommunicationEngine
    /// </summary>
    internal CommunicationEngine(IRepositoryNetwork repository, Action<string> log)
    {
      Log = log;
      m_Repository = repository;
      m_Repository.Broadcast += CommunicationEngine_Broadcast;
      connectToPeers(initialPeers);
      Log("CommunicationEngine has been started");
    }
    public void initP2PServer()
    {
      Task _servrer = WebSocketServer.Server(p2p_port, async _ws => await initConnection(_ws));
      Log($"listening websocket p2p port on: {p2p_port}");
    }
    #endregion

    #region INetworkAgentAPI
    /// <summary>
    /// cockets
    /// </summary>
    public List<WebSocketConnection> sockets { get; private set; } = new List<WebSocketConnection>();
    public void connectToPeers(Uri[] newPeers)
    {
      List<Task> _connectionJobs = new List<Task>();
      foreach (Uri peer in newPeers)
        _connectionJobs.Add(WebSocketClient.Connect(peer, async ws => await initConnection(ws), () => Log("connection failed")));
      Task.WaitAll(_connectionJobs.ToArray());
    }
    #endregion

    #region private
    private async Task initConnection(WebSocketConnection ws)
    {
      sockets.Add(ws);
      initMessageHandler(ws);
      initErrorHandler(ws);
      await write(ws, queryChainLengthMsg());
    }
    private void initMessageHandler(WebSocketConnection ws)
    {
      ws.onMessage = async (data) =>
      {
        Message message = Message.parse(data);
        Log($"Received message { message.stringify()}");
        switch (message.type)
        {
          case MessageType.QUERY_LATEST:
            await write(ws, responseLatestMsg());
            break;
          case MessageType.QUERY_ALL:
            await write(ws, responseChainMsg());
            break;
          case MessageType.RESPONSE_BLOCKCHAIN:
            handleBlockchainResponse(message);
            break;
        };
      };
    }
    private void initErrorHandler(WebSocketConnection ws)
    {
      ws.onClose = () => closeConnection(ws);
      ws.onError = () => closeConnection(ws);
    }
    private void closeConnection(WebSocketConnection _ws)
    {
      Log($"connection failed to peer: {_ws.ToString()}");
      sockets.Remove(_ws);
    }
    private void handleBlockchainResponse(Message message)
    {
      List<Block> receivedBlocks = message.Parse(); // JSON.parse(message.data).sort((b1, b2) => (b1.index - b2.index));
      Block latestBlockReceived = receivedBlocks[receivedBlocks.Count - 1];
      Block latestBlockHeld = m_Repository.getLatestBlock();
      if (latestBlockReceived.index > latestBlockHeld.index)
      {
        Log($"blockchain possibly behind. We got: {latestBlockHeld.index} Peer got: {latestBlockReceived.index}");
        if (latestBlockHeld.hash == latestBlockReceived.previousHash)
        {
          Log("We can append the received block to our chain");
          m_Repository.Add(latestBlockReceived);
          broadcast(responseLatestMsg());
        }
        else if (receivedBlocks.Count == 1)
        {
          Log("We have to query the chain from our peer");
          broadcast(queryAllMsg());
        }
        else
        {
          Log("Received blockchain is longer than current blockchain");
          replaceChain(receivedBlocks);
        }
      }
      else
        Log("received blockchain is not longer than received blockchain. Do nothing");
    }
    private void replaceChain(List<Block> newBlocks)
    {
      if (m_Repository.isValidChain(newBlocks) && newBlocks.Count > m_Repository.Count)
      {
        Log("Received blockchain is valid. Replacing current blockchain with received blockchain");
        m_Repository.replaceChain(newBlocks);
        broadcast(responseLatestMsg());
      }
      else
        Log("Received blockchain invalid");
    }
    private Message queryChainLengthMsg()
    {
      return new Message() { type = MessageType.QUERY_LATEST };
    }
    private Message queryAllMsg()
    {
      return new Message() { type = MessageType.QUERY_ALL };
    }
    private Message responseChainMsg()
    {
      return new Message()
      {
        type = MessageType.RESPONSE_BLOCKCHAIN,
        data = m_Repository.stringify()
      };
    }
    private Message responseLatestMsg()
    {
      return new Message()
      {
        type = MessageType.RESPONSE_BLOCKCHAIN,
        data = m_Repository.getLatestBlock().stringify() // JSON.stringify(getLatestBlock())
      };
    }
    private async Task write(WebSocketConnection ws, Message message) { await ws.send(message.stringify()); }
    private void broadcast(Message message)
    {
      List<Task> _jobs = new List<Task>();
      foreach (var socket in sockets)
        _jobs.Add(write(socket, message));
      Task.WaitAll(_jobs.ToArray());
    }
    private void CommunicationEngine_Broadcast(object sender, BlockchainStore.NewBlockEventArgs e)
    {
      Message _newMessage = new Message()
      {
        type = MessageType.RESPONSE_BLOCKCHAIN,
        data = e.Block.stringify() // JSON.stringify(getLatestBlock())
      };
      broadcast(_newMessage);
    }
    private Action<string> Log { get; }
    private int p2p_port { get; set; } = 6001;
    private Uri[] initialPeers { get; set; } = new Uri[] { };
    private IRepositoryNetwork m_Repository;
    #endregion

    #region IDisposable
    /// <summary>
    /// <see cref="IDisposable"/> implementation
    /// </summary>
    public void Dispose() { }
    #endregion

  }

}