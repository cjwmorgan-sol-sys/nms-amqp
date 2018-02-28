using System;
using System.Collections.Specialized;
using System.Collections.Generic;
using NUnit.Framework;
using NUnit.Framework.Interfaces;
using Apache.NMS;
using NMS.AMQP.Test.Util;
using NMS.AMQP.Test.Attribute;
using System.Text;

namespace NMS.AMQP.Test.TestCase
{
    [TestFixture]
    class ConsumerTest : BaseTestCase
    {
        const string DURABLE_TOPIC_NAME = "nms.durable.test";
        const string DURABLE_SUBSRIPTION_NAME = "uniqueSub";

        public override void Setup()
        {
            base.Setup();
            msgCount = 0;
            asyncEx = null;
            waiter = new System.Threading.ManualResetEvent(false);
        }


        protected void SendMessages(IMessageProducer producer, int messageCount)
        {
            ITextMessage message = producer.CreateTextMessage();

            for (int i = 0; i < messageCount; i++)
            {
                message.Text = "num:" + i;
                producer.Send(message);
            }
        }

        [Test]
        [ConnectionSetup(null, "default")]
        [SessionSetup("default", "default", AckMode = AcknowledgementMode.AutoAcknowledge)]
        [TopicSetup("default", "testdest1", Name = "nms.test")]
        [ConsumerSetup("default", "testdest1", "con1")]
        [ProducerSetup("default", "testdest1", "pro1")]
        public void TestReceiveMessageSync()
        {
            const int NUM_MSGS = 100;
            const int CONUSMER_TIMEOUT = 25; // 25 ms to pull from consumer.
            const int BACKOFF_SLEEP = 500; // 0.5 secs to sleep when consumer pull times out.
            using (IConnection connection = this.GetConnection())
            using (IMessageConsumer consumer = this.GetConsumer("con1"))
            using (IMessageProducer producer = this.GetProducer("pro1"))
            {
                try
                {
                    connection.ExceptionListener += DefaultExceptionListener;
                    connection.Start();

                    SendMessages(producer, NUM_MSGS);

                    ITextMessage textMessage = null;
                    IMessage message = null;
                    int backoffCount = 0;
                    DateTime start = DateTime.Now;
                    TimeSpan duration;
                    while (msgCount < NUM_MSGS && backoffCount < 3)
                    {
                        while ((message = consumer.Receive(TimeSpan.FromMilliseconds(CONUSMER_TIMEOUT))) != null)
                        {
                            textMessage = message as ITextMessage;
                            Assert.AreEqual("num:" + msgCount, textMessage.Text, "Received message out of order.");
                            msgCount++;
                        }
                        backoffCount++;
                        if (backoffCount < 3 && msgCount < NUM_MSGS)
                        {
                            System.Threading.Thread.Sleep(BACKOFF_SLEEP);
                        }
                    }

                    duration = (DateTime.Now - start);
                    int milis = Convert.ToInt32(duration.TotalMilliseconds);
                    Assert.AreEqual(NUM_MSGS, msgCount, "Received {0} messages out of {1} in {0}ms.", msgCount, NUM_MSGS, milis);

                }
                catch (Exception ex)
                {
                    this.PrintTestFailureAndAssert(this.GetMethodName(), "Unexpected Exception.", ex);
                }
            }
        }

        [Test]
        [ConnectionSetup(null, "default")]
        [SessionSetup("default", "default", AckMode = AcknowledgementMode.AutoAcknowledge)]
        [TopicSetup("default", "testdest1", Name = "nms.test")]
        [ConsumerSetup("default", "testdest1", "con1")]
        [ProducerSetup("default", "testdest1", "pro1", DeliveryMode = MsgDeliveryMode.NonPersistent)]
        public void TestReceiveStopStartMessageAsync()
        {
            const int NUM_MSGS = 100;
            const int MESSAGE_RECEIVE_TIMEOUT = 1000; // 1.5s

            //Apache.NMS.Util.Atomic<bool> stopped = new Apache.NMS.Util.Atomic<bool>(false);

            using (IConnection connection = this.GetConnection())
            using (IMessageConsumer consumer = this.GetConsumer("con1"))
            using (IMessageProducer producer = this.GetProducer("pro1"))
            {
                try
                {
                    MessageListener ackcallback = CreateListener(NUM_MSGS);
                    MessageListener callback = (m) =>
                    {
                        if (!connection.IsStarted)
                        {
                            waiter.Set();
                            throw new Exception("Received Message " + msgCount + " on stopped connection.");
                        }
                        ackcallback(m);
                        Logger.Info("MsgCount : " + msgCount);
                    };

                    consumer.Listener += callback;
                    connection.ExceptionListener += DefaultExceptionListener;
                    connection.Start();

                    ITextMessage message = producer.CreateTextMessage();

                    for (int i = 0; i < NUM_MSGS; i++)
                    {
                        message.Text = "num:" + i;
                        producer.Send(message);
                        if (i == NUM_MSGS / 2)
                        {
                            connection.Stop();
                            Logger.Info("Stopping Connection. At message " + i + ".");
                        }
                        else if (i == NUM_MSGS - 1)
                        {
                            Logger.Info("Starting Connection. At message " + i + ".");
                            connection.Start();

                        }
                    }

                    if (!waiter.WaitOne(MESSAGE_RECEIVE_TIMEOUT))
                    {
                        Assert.IsNull(asyncEx, "OnExceptionListener Event when raised. Message : {0}.", asyncEx?.Message);
                        Assert.Fail("Received {0} of {1} messages in {2}ms.", msgCount, NUM_MSGS, MESSAGE_RECEIVE_TIMEOUT);
                    }

                    Assert.IsNull(asyncEx, "OnExceptionListener Event when raised. Message : {0}.", asyncEx?.Message);
                    Assert.AreEqual(NUM_MSGS, msgCount, "Failed to Received All Messages sent.");

                }
                catch (Exception ex)
                {
                    Logger.Warn("Async execption: " + this.GetTestException(asyncEx));
                    this.PrintTestFailureAndAssert(this.GetMethodName(), "Unexpected Exception.", ex);
                }

            }
        }

        [Test]
        //[Repeat(1000)]
        [ConnectionSetup(null, "default")]
        [SessionSetup("default", "default", AckMode = AcknowledgementMode.ClientAcknowledge)]
        [TopicSetup("default", "testdest1", Name = "nms.test")]
        [ConsumerSetup("default", "testdest1", "con1")]
        [ProducerSetup("default", "testdest1", "pro1", DeliveryMode = MsgDeliveryMode.Persistent)]
        public void TestReceiveMessageAsyncClientAck()
        {
            const int NUM_MSGS = 100;
            const int MESSAGE_RECEIVE_TIMEOUT = 1000; // 1.5s

            //Apache.NMS.Util.Atomic<bool> stopped = new Apache.NMS.Util.Atomic<bool>(false);

            using (IConnection connection = this.GetConnection())
            using (ISession session = this.GetSession("default"))
            using (IMessageConsumer consumer = this.GetConsumer("con1"))
            using (IMessageProducer producer = this.GetProducer("pro1"))
            {
                try
                {
                    Assert.AreEqual(AcknowledgementMode.ClientAcknowledge, session.AcknowledgementMode, "Session acknowkedgement mode is not Client mode.");
                    MessageListener ackcallback = CreateListener(NUM_MSGS);
                    MessageListener callback = (m) =>
                    {
                        if (!connection.IsStarted)
                        {
                            waiter.Set();
                            throw new Exception("Received Message " + msgCount + " on stopped connection.");
                        }
                        m.Properties["NMS.AMQP.ACK.TYPE"] = 0;
                        m.Acknowledge();
                        ackcallback(m);
                        Logger.Info("MsgCount : " + msgCount);
                    };

                    consumer.Listener += callback;
                    connection.ExceptionListener += DefaultExceptionListener;
                    connection.Start();

                    ITextMessage message = producer.CreateTextMessage();

                    for (int i = 0; i < NUM_MSGS; i++)
                    {
                        message.Text = "num:" + i;
                        producer.Send(message);
                    }

                    if (!waiter.WaitOne(MESSAGE_RECEIVE_TIMEOUT))
                    {
                        Assert.IsNull(asyncEx, "OnExceptionListener Event when raised. Message : {0}.", asyncEx?.Message);
                        Assert.Fail("Received {0} of {1} messages in {2}ms.", msgCount, NUM_MSGS, MESSAGE_RECEIVE_TIMEOUT);
                    }

                    Assert.IsNull(asyncEx, "OnExceptionListener Event when raised. Message : {0}.", asyncEx?.Message);
                    Assert.AreEqual(NUM_MSGS, msgCount, "Failed to Received All Messages sent.");

                }
                catch (Exception ex)
                {
                    if (asyncEx != null)
                        Logger.Warn("Async execption: " + this.GetTestException(asyncEx));
                    this.PrintTestFailureAndAssert(this.GetMethodName(), "Unexpected Exception.", ex);
                }

            }
        }

        [Test]
        //[Repeat(1000)]
        [ConnectionSetup(null, "default")]
        [SessionSetup("default", "default", AckMode = AcknowledgementMode.AutoAcknowledge)]
        [TopicSetup("default", "testdest1", Name = "nms.test")]
        [ConsumerSetup("default", "testdest1", "con1")]
        [ProducerSetup("default", "testdest1", "pro1", DeliveryMode = MsgDeliveryMode.NonPersistent)]
        public void TestReceiveMessageAsync()
        {
            const int NUM_MSGS = 100;
            const int MESSAGE_RECEIVE_TIMEOUT = 1000; // 1.5s

            //Apache.NMS.Util.Atomic<bool> stopped = new Apache.NMS.Util.Atomic<bool>(false);

            using (IConnection connection = this.GetConnection())
            using (IMessageConsumer consumer = this.GetConsumer("con1"))
            using (IMessageProducer producer = this.GetProducer("pro1"))
            {
                try
                {
                    MessageListener ackcallback = CreateListener(NUM_MSGS);
                    MessageListener callback = (m) =>
                    {
                        if (!connection.IsStarted)
                        {
                            waiter.Set();
                            throw new Exception("Received Message " + msgCount + " on stopped connection.");
                        }
                        ackcallback(m);
                        Logger.Info("MsgCount : " + msgCount);
                    };

                    consumer.Listener += callback;
                    connection.ExceptionListener += DefaultExceptionListener;
                    connection.Start();

                    ITextMessage message = producer.CreateTextMessage();

                    for (int i = 0; i < NUM_MSGS; i++)
                    {
                        message.Text = "num:" + i;
                        producer.Send(message);
                    }

                    if (!waiter.WaitOne(MESSAGE_RECEIVE_TIMEOUT))
                    {
                        Assert.IsNull(asyncEx, "OnExceptionListener Event when raised. Message : {0}.", asyncEx?.Message);
                        Assert.Fail("Received {0} of {1} messages in {2}ms.", msgCount, NUM_MSGS, MESSAGE_RECEIVE_TIMEOUT);
                    }

                    Assert.IsNull(asyncEx, "OnExceptionListener Event when raised. Message : {0}.", asyncEx?.Message);
                    Assert.AreEqual(NUM_MSGS, msgCount, "Failed to Received All Messages sent.");

                }
                catch (Exception ex)
                {
                    Logger.Warn("Async execption: " + this.GetTestException(asyncEx));
                    this.PrintTestFailureAndAssert(this.GetMethodName(), "Unexpected Exception.", ex);
                }

            }
        }

        private void LogSummary(long[] missingCount, long[] lastId, long Msgbatch)
        {
            long missingMsgs = 0;
            long undeliveredMsgs = 0;

            for (int i = 0; i < missingCount.Length; i++)
            {
                missingMsgs += missingCount[i];
                undeliveredMsgs += Msgbatch - lastId[i] - 1;
            }

            long TotalMsgsDelivered = msgCount + missingMsgs + undeliveredMsgs;
            Logger.Warn(string.Format("Msgs Received: {0}, Msgs Lost: {1}, Msgs Remaining Delivery: {2} TotalMsgs: {3}",
                msgCount, missingMsgs, undeliveredMsgs, TotalMsgsDelivered));
        }

        [Test]
        //[Repeat(10)]
        [ConnectionSetup(null, "default")]
        [SessionSetup("default", "default", AckMode = AcknowledgementMode.AutoAcknowledge)]
        [TopicSetup("default", "testdest1", Name = "nms.test.1")]
        [TopicSetup("default", "testdest2", Name = "nms.test.2")]
        [TopicSetup("default", "testdest3", Name = "nms.test.3")]
        [TopicSetup("default", "testdest4", Name = "nms.test.4")]
        [ConsumerSetup("default", "testdest1", "con1")]
        [ConsumerSetup("default", "testdest2", "con2")]
        [ConsumerSetup("default", "testdest3", "con3")]
        [ConsumerSetup("default", "testdest4", "con4")]//*/
        [ProducerSetup("default", "testdest1", "pro1")]
        [ProducerSetup("default", "testdest2", "pro2")]
        [ProducerSetup("default", "testdest3", "pro3")]
        [ProducerSetup("default", "testdest4", "pro4")]//*/
        public void TestMultipleConsumerMessageListenerReceive()
        {
            const int NUM_MSGS = 100000;

            string[] conIds = new string[] { "con1", "con2", "con3", "con4" };
            string[] proIds = new string[] { "pro1", "pro2", "pro3", "pro4" };
            //string[] conIds = new string[] { "con1" };//, "con2" };
            //string[] proIds = new string[] { "pro1" };//, "pro2" };
            IList<IMessageConsumer> consumers = this.GetConsumers(conIds);
            IList<IMessageProducer> producers = this.GetProducers(proIds);
            int ProducerCount = producers.Count;
            int ConsumerCount = consumers.Count;
            long[] lastMsgId = new long[ConsumerCount];
            long[] MissingMsgCount = new long[ConsumerCount];
            long lostMsgCount = 0;
            int TotalMsgs = NUM_MSGS * ProducerCount;
            long undeliveredMsgCount = TotalMsgs;
            int timeout = Math.Max(TotalMsgs / 1000, 1) * 1100;
            MsgDeliveryMode mode = MsgDeliveryMode.NonPersistent;

            using (IConnection connection = this.GetConnection("default"))
            {
                for (int i = 0; i < ConsumerCount; i++)
                {
                    lastMsgId[i] = -1;
                    MissingMsgCount[i] = 0;
                }
                try
                {
                    MessageListener callback = CreateListener(TotalMsgs);
                    int index = 0;
                    foreach (IMessageConsumer consumer in consumers)
                    {
                        int num = index;
                        MessageListener countingCallback = (m) =>
                        {
                            long lastId = lastMsgId[num];
                            long msgId = ExtractMsgId(m.NMSMessageId);
                            if (msgId > lastId)
                            {
                                lastMsgId[num] = msgId;
                                if (lastId != -1)
                                {
                                    MissingMsgCount[num] += (msgId - (lastId + 1));
                                    lostMsgCount += (msgId - (lastId + 1));

                                }
                            }
                            callback(m);

                            // signal envent waiter when the last expected msg is delivered on all consumers
                            if (lostMsgCount + msgCount == TotalMsgs)
                            {
                                // signal if detected lost msgs from id gap and delivered msgs make the total msgs.
                                waiter?.Set();
                            }
                            else if (lastMsgId[num] == NUM_MSGS - 1)
                            {
                                // signal if final msg id on every consumer is detected.
                                undeliveredMsgCount -= NUM_MSGS;
                                if (undeliveredMsgCount <= 0)
                                    waiter?.Set();
                            }
                        };

                        consumer.Listener += countingCallback;
                        index++;
                    }

                    connection.ExceptionListener += DefaultExceptionListener;

                    connection.Start();

                    // send messages to Destinations
                    ITextMessage sendMsg = producers[0].CreateTextMessage();
                    for (int i = 0; i < NUM_MSGS; i++)
                    {
                        int link = 0;
                        foreach (IMessageProducer producer in producers)
                        {
                            sendMsg.Text = "Link: " + link + ", num:" + i;
                            link++;
                            producer.Send(sendMsg, mode, MsgPriority.Normal, TimeSpan.Zero);
                        }
                    }

                    if (!waiter.WaitOne(timeout))
                    {
                        if (mode.Equals(MsgDeliveryMode.NonPersistent))
                        {
                            if (StopOnAsyncFailure || msgCount != TotalMsgs) //&& asyncEx != null)
                            {
                                Logger.Warn(string.Format("Only received {0} of {1} messages in {2}ms.", msgCount, TotalMsgs, timeout));

                                LogSummary(MissingMsgCount, lastMsgId, NUM_MSGS);
                            }

                            Assert.IsNull(asyncEx, "Received unexpected asynchronous exception. Message : {0}", asyncEx?.Message);
                        }
                        else
                        {
                            Assert.Fail("Only received {0} of {1} messages in {2}ms.", msgCount, TotalMsgs, timeout);
                        }
                    }
                    else
                    {

                        if (StopOnAsyncFailure || msgCount != TotalMsgs) // && asyncEx != null)
                        {
                            Logger.Warn(string.Format("Only received {0} of {1} messages in {2}ms.", msgCount, TotalMsgs, timeout));

                            LogSummary(MissingMsgCount, lastMsgId, NUM_MSGS);
                        }

                        Assert.IsNull(asyncEx, "Received unexpected asynchronous exception. Message : {0}", asyncEx?.Message);

                        long ActualMsgs = mode.Equals(MsgDeliveryMode.NonPersistent) ? msgCount + lostMsgCount : msgCount;

                        Assert.AreEqual(TotalMsgs, ActualMsgs, "Only received {0} (delivered = {1}, lost = {2}) of {3} messages in {4}ms.", ActualMsgs, msgCount, lostMsgCount, TotalMsgs, timeout);
                    }
                }
                catch (Exception ex)
                {
                    this.PrintTestFailureAndAssert(GetTestMethodName(), "Unexpected Exception.", ex);
                }
            }
        }

        [Test]
        [ConnectionSetup(null, "default")]
        [ConnectionSetup(null, "sender")]
        [SessionSetup("sender", "s1", AckMode = AcknowledgementMode.AutoAcknowledge)]
        [SessionSetup("default", "default", AckMode = AcknowledgementMode.AutoAcknowledge)]
        [TopicSetup("default", "testdest1", Name = "nms.test")]
        [ConsumerSetup("default", "testdest1", "con1")]
        [ProducerSetup("s1", "testdest1", "pro1")]
        public void TestMessageListenerCallbackOnCloseMessageAsync()
        {
            const int NUM_MSGS = 100;

            //Apache.NMS.Util.Atomic<bool> stopped = new Apache.NMS.Util.Atomic<bool>(false);

            using (IConnection connection = this.GetConnection("default"))
            using (IMessageConsumer consumer = this.GetConsumer("con1"))
            using (IMessageProducer producer = this.GetProducer("pro1"))
            {
                try
                {
                    bool close = false;
                    MessageListener ackcallback = CreateListener(NUM_MSGS);
                    MessageListener callback = (m) =>
                    {
                        if (close)
                        {
                            close = false;
                            try
                            {
                                connection.Close();
                            }
                            catch (NMSException ex)
                            {
                                asyncEx = ex;
                            }
                        }

                        ackcallback(m);
                    };

                    consumer.Listener += callback;
                    connection.ExceptionListener += DefaultExceptionListener;
                    connection.Start();

                    ITextMessage message = producer.CreateTextMessage();

                    for (int i = 0; i < NUM_MSGS; i++)
                    {
                        message.Text = "num:" + i;
                        producer.Send(message);
                        if (i == NUM_MSGS / 2)
                        {
                            close = true;
                        }

                    }
                    connection.Stop();

                    Assert.NotNull(asyncEx, "Failed to receive Exception on MessageListener.");
                    Assert.True(asyncEx is IllegalStateException, "Failed to Recieve the correct IllegalStateException Exception, received : {0}", asyncEx);

                }
                catch (Exception ex)
                {
                    this.PrintTestFailureAndAssert(this.GetMethodName(), "Unexpected Exception.", ex);
                }
            }
        }


        [Test]
        [ConnectionSetup(null, "default")]
        [SessionSetup("default", "default")]
        [QueueSetup("default", "q1", Name = "nms.queue")]
        [ConsumerSetup("default", "q1", new[] { "c1", "c2", "c3" })]
        public void TestConsumerMessageListenerEventAddRemove()
        {
            IMessageConsumer consumer = null;
            IMessage msg = null;
            using (IConnection connection = GetConnection("default"))
            {
                consumer = GetConsumer("c1");
                try
                {
                    consumer.Listener += DefaultMessageListener;
                }
                catch (NMSException ex)
                {
                    this.PrintTestFailureAndAssert(this.GetTestMethodName(), "Unexpected Exception.", ex);
                }

                try
                {
                    msg = consumer.ReceiveNoWait();
                    Assert.Fail("Expected Exception when receiving synchously on asynchrous consumer.");
                }
                catch (NMSException ex)
                {
                    Assert.IsTrue(ex is IllegalStateException, "Expected exception was not IllealStateException. Exception: {0}", ex);
                    Assert.IsTrue(
                        ex.Message.StartsWith("Cannot synchronously receive message on a synchronous consumer"),
                        "Exception Message does not match. Message: {0}", ex.Message
                        );
                }

                connection.Start();
                consumer = GetConsumer("c2");
                try
                {
                    consumer.Listener += DefaultMessageListener;
                    Assert.Fail("Expected Exception when adding callback event on started connection.");
                }
                catch (NMSException ex)
                {
                    Assert.IsTrue(ex is IllegalStateException, "Expected exception was not IllealStateException. Exception: {0}", ex);
                    Assert.IsTrue(
                        ex.Message.StartsWith("Cannot add MessageListener to consumer"),
                        "Exception Message does not match. Message: {0}", ex.Message
                        );
                }

                try
                {
                    consumer.Listener -= DefaultMessageListener;
                    Assert.Fail("Expected Exception when removing callback event on started connection.");
                }
                catch (NMSException ex)
                {
                    Assert.IsTrue(ex is IllegalStateException, "Expected exception was not IllealStateException. Exception: {0}", ex);
                    Assert.IsTrue(
                        ex.Message.StartsWith("Cannot remove MessageListener to consumer"),
                        "Exception Message does not match. Message: {0}", ex.Message
                        );
                }


            }
        }

        /*
         * Test Durable Consumer Create (subscribe) and Unsubscribe capabilities.
         */
        [Test]
        [ConnectionSetup(null,"c1")]
        [SessionSetup("c1", "s1")]
        [TopicSetup("s1", "dt1", Name = DURABLE_TOPIC_NAME)]
        public void TestDurableConsumerCreateUnsubscribe()
        {
            string name = DURABLE_SUBSRIPTION_NAME;
            bool cleaned = false;
            IMessageConsumer durableConsumer = null;
            using (IConnection connection = GetConnection("c1"))
            using (ISession session = GetSession("s1"))
            {

                try
                {
                    ITopic topic = GetDestination("dt1") as ITopic;
                    durableConsumer = session.CreateDurableConsumer(topic, name, null, false);

                    try
                    {
                        IMessageConsumer other = session.CreateDurableConsumer(topic, name, null, false);
                        Assert.Fail("Expected exception for using the same subscription name {0} on the same connection", name);
                    }
                    catch (NMSException nmse)
                    {
                        Assert.AreEqual(nmse.ErrorCode, "nms:internal");
                    }

                    try
                    {
                        session.DeleteDurableConsumer(name);
                        Assert.Fail("Expected exception for deleting active subscription {0}.", name);
                    }
                    catch(IllegalStateException)
                    {
                        // pass
                    }

                    // make durable consumer inactive
                    durableConsumer.Close();

                    durableConsumer = null;

                    // unsubscribe the subscription
                    session.DeleteDurableConsumer(name);

                    cleaned = true;

                    try
                    {
                        session.DeleteDurableConsumer(name);
                        Assert.Fail("Expected InvalidDestinationException On Unsubscribe Operation for no existent subscription with name {0}", name);
                    }
                    catch (InvalidDestinationException ide)
                    {
                        // pass
                        Logger.Info(ide.Message);
                    }

                }
                catch (Exception ex)
                {
                    this.PrintTestFailureAndAssert(this.GetTestMethodName(), "Unexpected Exception.", ex);
                }
                finally
                {
                    if(durableConsumer != null)
                    {
                        durableConsumer.Close();
                    }

                    if (!cleaned)
                    {
                        try
                        {
                            session.DeleteDurableConsumer(name);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn(string.Format("Failed to clean up Durable Consumer {0}. Cause : {1}", name, ex));
                        }
                    }
                }
            }

        }

        // next test
    }
}