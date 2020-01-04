﻿

using EventSourcingOnAzureFunctions.Common.Binding;
using EventSourcingOnAzureFunctions.Common.EventSourcing;

using Microsoft.Azure.WebJobs.Extensions.DurableTask;

using Microsoft.Azure.WebJobs;
using System.Threading.Tasks;
using System.Collections.Generic;
using RetailBank.AzureFunctionApp.Account.Projections;
using RetailBank.AzureFunctionApp.Account.Classifications;
using System;
using Microsoft.Azure.WebJobs.Extensions.Http;
using System.Net.Http;
using EventSourcingOnAzureFunctions.Common.CQRS;
using RetailBank.AzureFunctionApp.Account.Events;

namespace RetailBank.AzureFunctionApp
{
    /// <summary>
    /// Functions that rely on the Durable Functions library for orchestration
    /// </summary>
    public partial class AccountFunctions
    {

        // 1 - Accrue interest for all 
        // Triggered by a timer "0 30 1 * * *"  at 01:30 AM every day
        [FunctionName(nameof(AccrueInterestFoAllAccountsTimer))]
        public static async Task AccrueInterestFoAllAccountsTimer(
            [TimerTrigger("0 30 1 * * *",
            RunOnStartup=false,
            UseMonitor =true)] TimerInfo accrueInterestTimer,
            [DurableClient] IDurableOrchestrationClient accrueInterestOrchestration,
            [Classification("Bank", "Account", "ALL", @"")] Classification clsAllAccounts
             )
        {
            // Get all the account numbers
            IEnumerable<string> allAccounts = await clsAllAccounts.GetAllInstanceKeys();

            await accrueInterestOrchestration.StartNewAsync(nameof(AccrueInterestForAllAccounts), allAccounts);
        }

        // Accrue Interest For All Accounts
        [FunctionName(nameof(AccrueInterestForAllAccounts))]
        public static async Task AccrueInterestForAllAccounts
            ([OrchestrationTrigger] IDurableOrchestrationContext context)
        {

            IEnumerable<string> allAccounts = context.GetInput<IEnumerable<string>>();

            if (null != allAccounts)
            {
                var accrualTasks = new List<Task<Tuple<string, bool>>>();
                foreach (string accountNumber in allAccounts)
                {
                    Task<Tuple<string, bool>> accrualTask = context.CallActivityAsync<Tuple<string, bool>>(nameof(AccrueInterestForSpecificAccount), accountNumber);
                    accrualTasks.Add(accrualTask);
                }

                // Perform all the accruals in parrallel
                await Task.WhenAll(accrualTasks);

                List<string> failedAccruals = new List<string>();
                foreach (var accrualTask in accrualTasks)
                {
                    if (!accrualTask.Result.Item2)
                    {
                        failedAccruals.Add(accrualTask.Result.Item1);
                    }
                }

                // Try a second pass - using failedAccruals.Count ?
                if (failedAccruals.Count > 0)
                {
                    throw new Exception("Not all account accruals succeeded");
                }

            }

        }

        //AccrueInterestForSpecificAccount
        [FunctionName(nameof(AccrueInterestForSpecificAccount))]
        public static async Task<Tuple<string, bool>> AccrueInterestForSpecificAccount
            ([ActivityTrigger] IDurableActivityContext accrueInterestContext)
        {

            const decimal DEBIT_INTEREST_RATE = 0.001M;
            const decimal CREDIT_INTEREST_RATE = 0.0005M;

            string accountNumber = accrueInterestContext.GetInput<string>();

            if (!string.IsNullOrEmpty(accountNumber))
            {
                EventStream bankAccountEvents = new EventStream(new EventStreamAttribute("Bank", "Account", accountNumber));
                if (await bankAccountEvents.Exists())
                {
                    // Has the accrual been done today for this account?
                    Classification clsAccruedToday = new Classification(new ClassificationAttribute("Bank", "Account", accountNumber, nameof(InterestAccruedToday)));
                    ClassificationResponse isAccrued = await clsAccruedToday.Classify<InterestAccruedToday>();
                    if (isAccrued.Result != ClassificationResponse.ClassificationResults.Include)
                    {
                        // Get the account balance
                        Projection prjBankAccountBalance = new Projection(new ProjectionAttribute("Bank", "Account", accountNumber, nameof(Balance)));

                        // Get the current account balance, as at midnight
                        Balance projectedBalance = await prjBankAccountBalance.Process<Balance>(DateTime.Today);
                        if (null != projectedBalance)
                        {
                            Account.Events.InterestAccrued evAccrued = new Account.Events.InterestAccrued()
                            {
                                Commentary = $"Daily scheduled interest accrual",
                                AccrualEffectiveDate = DateTime.Today  // set the accrual to midnight today  
                            };
                            // calculate the accrual amount
                            if (projectedBalance.CurrentBalance >= 0)
                            {
                                // Using the credit rate
                                evAccrued.AmountAccrued = CREDIT_INTEREST_RATE * projectedBalance.CurrentBalance;
                                evAccrued.InterestRateInEffect = CREDIT_INTEREST_RATE;
                            }
                            else
                            {
                                // Use the debit rate
                                evAccrued.AmountAccrued = DEBIT_INTEREST_RATE * projectedBalance.CurrentBalance;
                                evAccrued.InterestRateInEffect = DEBIT_INTEREST_RATE;
                            }

                            try
                            {
                                await bankAccountEvents.AppendEvent(evAccrued, isAccrued.AsOfSequence);
                            }
                            catch (EventSourcingOnAzureFunctions.Common.EventSourcing.Exceptions.EventStreamWriteException exWrite)
                            {
                                // We can't be sure this hasn't already run... 
                                return new Tuple<string, bool>(accountNumber, false);
                            }

                        }
                    }
                }
            }
            return new Tuple<string, bool>(accountNumber, true);
        }


        // 2- Apply interest for all accounts
        [FunctionName(nameof(ApplyInterestForAllAccountsTrigger))]
        public static async Task ApplyInterestForAllAccountsTrigger(
            [HttpTrigger(AuthorizationLevel.Function, "POST", Route = @"ApplyInterestForAllAccounts")]HttpRequestMessage req,
            [DurableClient] IDurableOrchestrationClient accrueInterestOrchestration,
            [Classification("Bank", "Account", "ALL", @"")] Classification clsAllAccounts)
        {

            // Get all the account numbers
            IEnumerable<string> allAccounts = await clsAllAccounts.GetAllInstanceKeys();

            await accrueInterestOrchestration.StartNewAsync(nameof(ApplyInterestForAllAccounts), allAccounts);

        }

        /// <summary>
        /// Apply interest to all of the bank accounts in the system
        /// </summary>
        /// <param name="context">
        /// The durable functions orchestration for this command
        /// </param>
        [FunctionName(nameof(ApplyInterestForAllAccounts))]
        public static async Task ApplyInterestForAllAccounts(
            [OrchestrationTrigger] IDurableOrchestrationContext context)
        {

            IEnumerable<string> allAccounts = context.GetInput<IEnumerable<string>>();

            if (null != allAccounts)
            {
                var overdraftForInterestTasks = new List<Task<Tuple<string, bool>>>();
                foreach (string accountNumber in allAccounts)
                {
                    Task<Tuple<string, bool>> overdraftTask = context.CallActivityAsync<Tuple<string, bool>>(nameof(SetOverdraftForInterestForSpecificAccount), accountNumber);
                    overdraftForInterestTasks.Add(overdraftTask);
                }

                // Perform all the overdraft extension operations in parrallel
                await Task.WhenAll(overdraftForInterestTasks);

                // Only actually pay interest for those where the last step succeeded..
                var payInterestTasks = new List<Task>();
                foreach (var overdraftTask in overdraftForInterestTasks)
                {
                    if (overdraftTask.Result.Item2)
                    {
                        Task interestTask = context.CallActivityAsync(nameof(PayInterestForSpecificAccount), overdraftTask.Result.Item1);
                    }
                }

                await Task.WhenAll(payInterestTasks);
            }
        }


        [FunctionName(nameof(SetOverdraftForInterestForSpecificAccount))]
        public static async Task<Tuple<string, bool >> SetOverdraftForInterestForSpecificAccount
          ([ActivityTrigger] IDurableActivityContext interestOverdraftContext)
        {

            string accountNumber = interestOverdraftContext.GetInput<string>();
            bool success = true;

            Command cmdPayInterest = new Command(
                new CommandAttribute("Bank",
                    "Pay Interest",
                    interestOverdraftContext.InstanceId )
                );

            if (!string.IsNullOrWhiteSpace(accountNumber))
            {

                string result = "No overdraft required";

                await cmdPayInterest.InitiateStep(AccountCommands.COMMAND_STEP_OVERDRAFT ,
                    "Bank",
                    "Account",
                    accountNumber );

                // run the "set overdraft limit for interest" function 
                // 1- Get interest due...
                Projection prjInterestDue = new Projection(
                    new ProjectionAttribute(
                        "Bank",
                        "Account",
                        accountNumber,
                        nameof(InterestDue)
                        )
                    );

                // get the interest owed / due as now
                InterestDue interestDue = await prjInterestDue.Process<InterestDue>();
                if (null != interestDue)
                {
                    // if the interest due is negative we need to make sure the account has sufficient balance
                    if (interestDue.Due < 0.00M)
                    {
                        Projection prjBankAccountBalance = new Projection(
                            new ProjectionAttribute(
                                "Bank",
                                "Account",
                                accountNumber,
                                nameof(InterestDue)
                                )
                            );

                        Balance balance = await prjBankAccountBalance.Process<Balance>();
                        if (null != balance)
                        {
                            decimal availableBalance = balance.CurrentBalance;

                            // is there an overdraft?
                            Projection prjBankAccountOverdraft = new Projection(
                                        new ProjectionAttribute(
                                        "Bank",
                                        "Account",
                                        accountNumber,
                                        nameof(OverdraftLimit)
                                        )
                                );

                            OverdraftLimit overdraft = await prjBankAccountOverdraft.Process<OverdraftLimit>();
                            if (null != overdraft)
                            {
                                availableBalance += overdraft.CurrentOverdraftLimit;
                            }

                            if (availableBalance < interestDue.Due)
                            {
                                // Force an overdraft extension
                                EventStream bankAccountEvents = new EventStream(
                                    new EventStreamAttribute(
                                        "Bank",
                                        "Account",
                                        accountNumber
                                        )
                                    );

                                decimal newOverdraft = overdraft.CurrentOverdraftLimit;
                                decimal extension = 10.00M + Math.Abs(interestDue.Due % 10.00M);
                                OverdraftLimitSet evNewLimit = new OverdraftLimitSet()
                                {
                                    OverdraftLimit = newOverdraft + extension,
                                    Commentary = $"Overdraft extended to pay interest of {interestDue.Due} ",
                                    Unauthorised = true
                                };

                                result = $"Overdraft set to {evNewLimit.OverdraftLimit } ({evNewLimit.Commentary})";

                                try
                                {
                                    await bankAccountEvents.AppendEvent(evNewLimit, balance.CurrentSequenceNumber);
                                }
                                catch (EventSourcingOnAzureFunctions.Common.EventSourcing.Exceptions.EventStreamWriteException exWrite)
                                {
                                    success = false;
                                }

                                if (success)
                                {
                                    await cmdPayInterest.StepCompleted(AccountCommands.COMMAND_STEP_OVERDRAFT,
                                            result,
                                            "Bank",
                                            "Account",
                                            accountNumber);
                                }
                            }
                        }
                    }
                }
            }

            return new Tuple<string, bool>(accountNumber, success);
        }


        [FunctionName(nameof(PayInterestForSpecificAccount))]
        public static async Task PayInterestForSpecificAccount
            ([ActivityTrigger] IDurableActivityContext payInterestContext)
        {

            string accountNumber = payInterestContext.GetInput<string>();

            Command cmdPayInterest = new Command(
            new CommandAttribute("Bank",
                "Pay Interest",
                payInterestContext.InstanceId)
            );

            if (!string.IsNullOrWhiteSpace(accountNumber))
            {
                string result = "";

                await cmdPayInterest.InitiateStep(AccountCommands.COMMAND_STEP_PAY_INTEREST,
                        "Bank",
                        "Account",
                        accountNumber);

                // 1- Get interest due...
                Projection prjInterestDue = new Projection(
                    new ProjectionAttribute(
                        "Bank",
                        "Account",
                        accountNumber,
                        nameof(InterestDue)
                        )
                    );

                // get the interest owed / due as now
                InterestDue interestDue = await prjInterestDue.Process<InterestDue>();
                if (null != interestDue)
                {
                    // pay the interest
                    decimal amountToPay = decimal.Round(interestDue.Due, 2, MidpointRounding.AwayFromZero);
                    if (amountToPay != 0.00M)
                    {
                        EventStream bankAccountEvents = new EventStream(
                            new EventStreamAttribute(
                                "Bank",
                                "Account",
                                accountNumber
                                )
                            );

                        InterestPaid evInterestPaid = new InterestPaid()
                        {
                            AmountPaid = decimal.Round(interestDue.Due, 2, MidpointRounding.AwayFromZero),
                            Commentary = $"Interest due {interestDue.Due} as at {interestDue.CurrentSequenceNumber}"
                        };
                        await bankAccountEvents.AppendEvent(evInterestPaid);

                        result = $"Interest paid: {evInterestPaid.AmountPaid} ({evInterestPaid.Commentary})";
                    }
                }

                await cmdPayInterest.StepCompleted(AccountCommands.COMMAND_STEP_PAY_INTEREST,
                            result,
                            "Bank",
                            "Account",
                            accountNumber);

            }
        }
    }
}
