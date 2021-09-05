using System;
using System.Linq;
using System.Threading.Tasks;
using API.DTOs;
using API.entities;
using API.Extensions;
using API.Interfaces;
using AutoMapper;
using Microsoft.AspNetCore.SignalR;

namespace API.SignalR
{
    public class MessageHub : Hub
    {
        private readonly IMessageRep _messageRep;
        private readonly IMapper _mapper;
        private readonly IUserRep _userRep;
        private readonly IHubContext<PresenceHub> _presenceHub;
        private readonly PresenceTracker _tracker;

        public MessageHub(IMessageRep messageRep, IMapper mapper,
        IUserRep userRep, IHubContext<PresenceHub> presenceHub,
        PresenceTracker tracker)
        {
            _messageRep = messageRep;
            _mapper = mapper;
            _userRep = userRep;
            _presenceHub = presenceHub;
            _tracker = tracker;
        }

        public override async Task OnConnectedAsync()
        {
            var httpContext = Context.GetHttpContext();
            var otherUser = httpContext.Request.Query["user"].ToString();
            var groupName = GetGroupName(Context.User.GetUsername(), otherUser);
            await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
            var group = await AddToGroup(groupName);
            await Clients.Group(groupName).SendAsync("UpdatedGroup", group);

            var messages = await _messageRep.
                GetMessageThread(Context.User.GetUsername(), otherUser);

            await Clients.Caller.SendAsync("ReceiveMessageThread", messages);    
        }

        public override async Task OnDisconnectedAsync(Exception exception)
        {
            var group = await RemoveFromMessageGroup();
            await Clients.Group(group.Name).SendAsync("UpdatedGroup", group);
            await base.OnDisconnectedAsync(exception);
        }

        public async Task SendMessage(CreateMessageDto createMessageDto)
        {
             var username =Context.User.GetUsername();

            if (username == createMessageDto.RecipientUsername.ToLower())
                throw new HubException("You cannot send messages to yourself");

            var sender = await _userRep.GetUserByUsernameAsync(username);
            var recipient = await _userRep.GetUserByUsernameAsync(createMessageDto.RecipientUsername);

            if (recipient == null) throw new HubException("Not found user");

            var message = new Message
            {
                Sender = sender,
                Recipient = recipient,
                SenderUsername = sender.UserName,
                RecipientUsername = recipient.UserName,
                Content = createMessageDto.Content
            };

            var groupName = GetGroupName(sender.UserName, recipient.UserName);

            var group = await _messageRep.GetMessageGroup(groupName);

            if (group.Connections.Any(x => x.Usename == recipient.UserName))
            {
                message.DateRead = DateTime.UtcNow;
            }
            else
            {
                var connections = await _tracker.GetConnectionsForUser(recipient.UserName);
                if (connections != null)
                {
                    await _presenceHub.Clients.Clients(connections).SendAsync("NewMessageReceived",
                    new {username = sender.UserName, knowAs = sender.KnowAs});
                }
            }

            _messageRep.AddMessage(message);

            if (await _messageRep.SaveAllAsync())
            {
              await Clients.Group(groupName).SendAsync("NewMessage", _mapper.Map<MessgeDto>(message));
            } 
        }

        private async Task<Group> AddToGroup(string groupName)
        {
            var group = await _messageRep.GetMessageGroup(groupName);
            var connection = new Connection(Context.ConnectionId, Context.User.GetUsername());

            if (group == null)
            {
                group = new Group(groupName);
                _messageRep.AddGroup(group);
            }

            group.Connections.Add(connection);

            if (await _messageRep.SaveAllAsync()) return group;

            throw new HubException("Falied to join group");
        }

        private async Task<Group> RemoveFromMessageGroup()
        {
            var group = await _messageRep.GetGroupForConnection(Context.ConnectionId);
            var connection = group.Connections.FirstOrDefault(x => x.ConnectionId == Context.ConnectionId);
            _messageRep.RemoveConnection(connection);
           if (await _messageRep.SaveAllAsync()) return group;

           throw new HubException("Failed to remove from group");
        }

        private string GetGroupName(string caller, string other)
        {
            var stringCompare = string.CompareOrdinal(caller, other) > 0;
            return stringCompare ? $"{caller}-{other}" : $"{other}-{caller}";
        }
        
    }
}