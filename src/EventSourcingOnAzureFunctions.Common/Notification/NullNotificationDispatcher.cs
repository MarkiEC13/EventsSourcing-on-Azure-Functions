﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using EventSourcingOnAzureFunctions.Common.EventSourcing.Interfaces;

namespace EventSourcingOnAzureFunctions.Common.Notification
{
    /// <summary>
    /// A notification dispatcher that does not do anything
    /// </summary>
    /// <remarks>
    /// This can be used in unit testing or if you have an application that you do not want to 
    /// do any notification
    /// </remarks>
    public class NullNotificationDispatcher
        : INotificationDispatcher
    {
        public Task NewEntityCreated(IEventStreamIdentity newEntity)
        {
            // do nothing
            return Task.CompletedTask;
        }

        public Task NewEventAppended(IEventStreamIdentity targetEntity, string eventType, int sequenceNumber)
        {
            // do nothing
            return Task.CompletedTask;
            
        }
    }
}