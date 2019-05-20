# Event Sourcing on Azure Functions
A library to demonstrate doing Event Sourcing as a data persistence mechanism for Azure Functions.

## Introduction to event sourcing

At its very simplest, event sourcing is a way of storing state (for an entity) which works by storing the sequential history of all the events that have occured to that entity.  Changes to the entity are written as new events appended to the end of the event stream for the entity. 

When a query or business process needs to use the current state of the entity it gets this by running a projection over the event stream which is a very simple piece of code which, for each event, decides (a) do I care about this type of event and (b) if so what do I do when I receive it.

There is a 50 minute talk that covers this on [YouTube](https://www.youtube.com/watch?v=kpM5gCLF1Zc)

## Chosen technologies

Because an event stream is an inherently append only system the storage technology underlying it is [AppendBlob](https://docs.microsoft.com/en-us/rest/api/storageservices/append-block) - a special type of Blob storage which only allows for blocks to be appended to the end of the blob.  Each blob can store up to 50,000 events and the container path can be nested in the same way as any other Azure Blob storage.

The [azure functions](https://azure.microsoft.com/en-us/services/functions/) code is based on version 2.0 of the azure functions SDK and is written in C#.

## End goal

The goal is to be able to interact with the event streams for entities without an extra plumbing in the azure function itself - with both access to event streams and to run projections being via bound variables that are instantiated when the azure function is executed.

## Comparison to other event sourcing technologies

In this library the state of an entity has to be retrieved on demand - this is to allow for the functions application to be spun down to nothing and indeed for multiple independent azure functions applications to use the same 

## Requirements

In order to use this library you will need an Azure account with the ability to create a storage container and to host an [azure functions](https://azure.microsoft.com/en-us/services/functions/) application.
