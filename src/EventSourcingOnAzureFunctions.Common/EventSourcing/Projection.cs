﻿using EventSourcingOnAzureFunctions.Common.Binding;
using EventSourcingOnAzureFunctions.Common.EventSourcing.Interfaces;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace EventSourcingOnAzureFunctions.Common.EventSourcing
{
    public class Projection
         : IEventStreamIdentity
    {

        private readonly IProjectionProcessor _projectionProcessor = null;


        private readonly string _domainName;
        /// <summary>
        /// The domain in which this event stream that we are running the projection over is located
        /// </summary>
        public string DomainName
        {
            get
            {
                return _domainName;
            }
        }


        private readonly string _entityTypeName;
        /// <summary>
        /// The type of entity for which this event stream that we are running the projection over pertains
        /// </summary>
        public string EntityTypeName
        {
            get
            {
                return _entityTypeName;
            }
        }

        private readonly string _instanceKey;
        /// <summary>
        /// The specific uniquely identitified instance of the entity to which this event stream 
        /// that we are running the projection over pertains
        /// </summary>
        public string InstanceKey
        {
            get
            {
                return _instanceKey;
            }
        }

        /// <summary>
        /// The type of the projection we are going to run 
        /// </summary>
        private readonly string _projectionTypeName;
        public string ProjectionTypeName
        {
            get
            {
                return _projectionTypeName;
            }
        }


        public async Task<TProjection> Process<TProjection>() where TProjection : IProjection, new()
        {
            if (null != _projectionProcessor )
            {
                return await _projectionProcessor.Process<TProjection>(); 
            }
            else
            {
                return await Task.FromException<TProjection>(new Exception("Projection processor not initialised"));
            }
        }


        private readonly string _connectionStringName;
        public string ConnectionStringName
        {
            get
            {
                return _connectionStringName;
            }
        }

        /// <summary>
        /// Create the projection from the attribute linked to the function parameter
        /// </summary>
        /// <param name="attribute">
        /// The attribute describing which projection to run
        /// </param>
        public Projection(ProjectionAttribute attribute,
            string connectionStringName = "")
        {

            _domainName = attribute.DomainName;
            _entityTypeName  = attribute.EntityTypeName ;
            _instanceKey = attribute.InstanceKey;
            _projectionTypeName = attribute.ProjectionTypeName;


            if (string.IsNullOrWhiteSpace(connectionStringName))
            {
                _connectionStringName = ConnectionStringNameAttribute.DefaultConnectionStringName(attribute);
            }
            else
            {
                _connectionStringName = connectionStringName;
            }

            if (null == _projectionProcessor)
            {

                // TODO : Allow for different backing technologies... currently just AppendBlob
                // _projectionProcessor = CQRSAzure.EventSourcing.Azure.Blob.Untyped.BlobEventStreamReaderUntyped.CreateProjectionProcessor(attribute,
                //    ConnectionStringNameAttribute.DefaultBlobStreamSettings(_domainName, _aggregateTypeName));
            }

        }
    }
}