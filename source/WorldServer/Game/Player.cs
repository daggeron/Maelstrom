﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using Shared.Database.Datacentre;
using Shared.Game;
using WorldServer.Network;
using WorldServer.Network.Message;

namespace WorldServer.Game
{
    public class Player : Actor
    {
        public WorldSession Session { get; }
        public CharacterInfo Character { get; }

        public bool IsLogin { get; set; } = true;
        public bool IsLoading { get; set; } = true;
        
        private readonly QueuedCounter<byte> spawnIndex = new QueuedCounter<byte>(0);
        private readonly Dictionary<uint, byte> spawnIndexLookup = new Dictionary<uint, byte>(byte.MaxValue);

        private WorldPosition pendingTeleportPosition;

        public Player(WorldSession session, CharacterInfo character)
            : base(character.ActorId, ActorType.Player)
        {
            Session   = session;
            Character = character;
            Position  = character.SpawnPosition;
        }

        public override void AddVisibleActor(Actor actor)
        {
            base.AddVisibleActor(actor);

            // inital territory spawns are sent in bulk from packet handler after territory loading
            if (IsLoading)
                return;

            SendActor(actor);
        }

        public override void RemoveVisibleActor(Actor actor)
        {
            Session.Send(actor.Id, actor.Id, new ServerActorAction1
            {
                Action = ActorAction.ActorRemove,
                Parameter1 = 1
            });

            if (actor.IsPlayer)
            {
                if (spawnIndexLookup.TryGetValue(actor.Id, out byte index))
                {
                    spawnIndexLookup.Remove(actor.Id);
                    spawnIndex.EnqueueValue(index);
                }
            }
            
            base.RemoveVisibleActor(actor);
        }

        private byte GetSpawnIndex(uint actorId)
        {
            // local actor always has an index of 0
            if (actorId == Id)
                return 0;
            
            if (!spawnIndexLookup.TryGetValue(actorId, out byte index))
            {
                index = spawnIndex.DequeueValue();
                spawnIndexLookup.Add(actorId, index);
            }

            return index;
        }

        private void SendActor(Actor actor)
        {
            if (actor.IsPlayer)
            {
                Session.Send(actor.Id, Id,
                    new ServerPlayerSpawn
                    {
                        SpawnIndex = GetSpawnIndex(actor.Id),
                        Position   = Position,
                        Character  = actor.ToPlayer.Character
                    });
            }
            else
            {
                // TODO: NPC spawns have a seperate packet
            }
        }

        public void OnLogin()
        {
            Debug.Assert(IsLogin);
            
            Session.Send(new ServerPlayerSetup
            {
                Character = Character
            });

            Session.Send(new ServerClassSetup
            {
                ClassJobId = Character.ClassJobId,
                Level      = Character.Classes[Character.ClassId].Level
            });

            // MOTD
            Session.Send(new ServerMessage
            {
                Message = "Welcome to this Maelstrom server!\n"
                    + "Find out about this server project at https://www.github.com/Rawaho/Maelstrom"
            });

            // TODO: client hangs without these sent
            Session.Send(new ServerUnknown01FB());
            Session.Send(new ServerUnknown01FD());
            
            Session.Send(new ServerQuestJournalActiveList());
            Session.Send(new ServerQuestJournalCompleteList());
        }

        public override void OnAddToMap()
        {
            base.OnAddToMap();
            pendingTeleportPosition = null;

            Session.Send(new ServerNewWorld
            {
                ActorId = Character.ActorId
            });
            
            Session.Send(new ServerTerritorySetup
            {
                WeatherId     = 1,
                WorldPosition = Position
            });
        }

        public override void OnRemoveFromMap()
        {
            base.OnRemoveFromMap();

            if (pendingTeleportPosition != null)
            {
                Position  = pendingTeleportPosition;
                IsLoading = true;
                AddToMap();
            }
        }

        public void SendVisible()
        {
            foreach (Actor actor in visibleActors)
                SendActor(actor);
        }

        public void TeleportTo(WorldPosition newPosition)
        {
            if (pendingTeleportPosition != null)
                return;

            #if DEBUG
                Console.WriteLine($"Teleporting player '{Character.Name}' to Territory: {newPosition.TerritoryId}, "
                    + $"X: {newPosition.Offset.X}, Y: {newPosition.Offset.Y}, Z: {newPosition.Offset.Z}");
            #endif

            // TODO: validate position
            pendingTeleportPosition = newPosition;

            if (Position.TerritoryId != newPosition.TerritoryId)
            {
                // shows black loading screen with territory name
                Session.Send(new ServerTerritoryPending
                {
                    TerritoryId  = newPosition.TerritoryId,
                    LogMessageId = 0u
                });
            }

            Map.RemoveActor(this);
        }
    }
}
