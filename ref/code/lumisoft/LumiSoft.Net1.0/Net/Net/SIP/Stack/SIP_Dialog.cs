using System;
using System.Collections.Generic;
using System.Text;
using System.Timers;

using LumiSoft.Net.SIP.Message;

namespace LumiSoft.Net.SIP.Stack
{
    #region Delegates

    /// <summary>
    /// Represents method what will handle ResponseReceived event.
    /// </summary>
    /// <param name="e">Event data.</param>
    public delegate void SIP_RequestReceivedEventHandler(SIP_RequestReceivedEventArgs e);

    #endregion

    /// <summary>
    /// Implements SIP dialog. Defined in rfc 3261.12.
    /// </summary>
    /// <remarks>
    /// <img src="../images/SIP_Dialog.gif" />
    /// </remarks>
    public class SIP_Dialog : IDisposable
    {
        #region class Uac2xxRetransWaitEntry

        /// <summary>
        /// This class holds UAC INVITE 2xx response retransmit wait data.
        /// </summary>
        private class Uac2xxRetransWaitEntry : IDisposable
        {
            private SIP_Dialog   m_pDialog          = null;
            private SIP_Response m_p2xxResponse     = null;
            private SIP_Request  m_pAckRequest      = null;
            private Timer        m_pUac2xxWaitTimer = null;

            /// <summary>
            /// Default constructor.
            /// </summary>
            /// <param name="dialog">Owner dialog.</param>
            /// <param name="x2xxResponse">2xx response.</param>
            /// <param name="ack">ACK request.</param>
            public Uac2xxRetransWaitEntry(SIP_Dialog dialog,SIP_Response x2xxResponse,SIP_Request ack)
            {
                m_pDialog      = dialog;
                m_p2xxResponse = x2xxResponse;
                m_pAckRequest  = ack;

                m_pUac2xxWaitTimer = new Timer(64 * SIP_TimerConstants.T1);
                m_pUac2xxWaitTimer.AutoReset = false;
                m_pUac2xxWaitTimer.Elapsed += new ElapsedEventHandler(m_pUac2xxWaitTimer_Elapsed);
                m_pUac2xxWaitTimer.Enabled = true;

                dialog.Stack.Logger.AddDebug("Dialog(id='" + dialog.DialogID + "') INVITE 2xx retransmission wait timer started.");
            }

            #region method Dispose

            /// <summary>
            /// Cleans up any resources being used.
            /// </summary>
            public void Dispose()
            {
                if(m_pUac2xxWaitTimer != null){
                    m_pUac2xxWaitTimer.Dispose();
                    m_pUac2xxWaitTimer = null;
                }

                m_pDialog.m_pUac2xxWaits.Remove(this);
            }

            #endregion


            #region Events Handling

            #region method m_pUac2xxWaitTimer_Elapsed

            /// <summary>
            /// Is called when UAC must end waiting 2xx responses retransmittion.
            /// </summary>
            /// <param name="sender">Sender.</param>
            /// <param name="e">Event data.</param>
            private void m_pUac2xxWaitTimer_Elapsed(object sender,ElapsedEventArgs e)
            {
                /* RFC 3261 13.2.2.4.
                    The UAC core considers the INVITE transaction completed 64*T1 seconds after 
                    the reception of the first 2xx response. Once the INVITE transaction is considered 
                    completed by the UAC core, no more new 2xx responses are expected to arrive.
                */
                
                m_pDialog.Stack.Logger.AddDebug("Dialog(id='" + m_pDialog.DialogID + "') INVITE 2xx retransmission wait timer stopped.");

                Dispose();
            }

            #endregion

            #endregion


            #region Properties Implementation

            /// <summary>
            /// Gets 2xx response which corresponding ACK this entry holds.
            /// </summary>
            public SIP_Response x2xxResponse
            {
                get{ return m_p2xxResponse; }
            }

            /// <summary>
            /// Gets 2xx response corresponding ACK request.
            /// </summary>
            public SIP_Request Ack
            {
                get{ return m_pAckRequest; }
            }

            #endregion
        }

        #endregion

        private SIP_DialogState              m_DialogState            = SIP_DialogState.Early;
        private SIP_Stack                    m_pStack                 = null;
        private string                       m_Method                 = "";
        private string                       m_CallID                 = "";
        private string                       m_LocalTag               = "";
        private string                       m_RemoteTag              = "";
        private int                          m_LocalSequenceNo        = 0;
        private int                          m_RemoteSequenceNo       = 0;
        private string                       m_LocalUri               = "";
        private string                       m_RemoteUri              = "";
        private string                       m_RemoteTarget           = "";
        private SIP_t_AddressParam[]         m_pRouteSet              = null;
        private bool                         m_IsSecure               = false;
        private bool                         m_IsServer               = false;
        private object                       m_pTag                   = null;
        private DateTime                     m_CreateTime;
        private bool                         m_IsDisposed             = false;
        private Timer                        m_pUas2xxRetransmitTimer = null;
        private DateTime                     m_2xxRetransmitStartTime;
        private SIP_Response                 m_p2xxRetransmitResponse = null;
        private List<SIP_Transaction>        m_pTransactions          = null;
        private List<Uac2xxRetransWaitEntry> m_pUac2xxWaits           = null;
        private Timer                        m_pEarlyDialogTimer      = null;
        private Timer                        m_pSessionTimer          = null;
        private Timer                        m_pSessionRefreshTimer   = null;
        private byte[]                       m_LocalSDP               = null;

        /// <summary>
        /// Default constructor.
        /// </summary>
        /// <param name="stack">Owner stack.</param>
        /// <param name="transaction">SIP transaction that careate this dialog.</param>
        /// <param name="response">SIP response what caused dialog creation.</param>
        /// <exception cref="ArgumentNullException">Is raised if any of the arguments is null.</exception>
        internal SIP_Dialog(SIP_Stack stack,SIP_Transaction transaction,SIP_Response response)
        {
            if(stack == null){
                throw new ArgumentNullException("stack");
            }
            if(transaction == null){
                throw new ArgumentNullException("transaction");
            }
            if(response == null){
                throw new ArgumentNullException("response");
            }
            
            m_pStack = stack;
            m_pTransactions = new List<SIP_Transaction>();
            m_pUac2xxWaits = new List<Uac2xxRetransWaitEntry>();
            
            // Add transaction to transactions collection.
            transaction.Terminated += new EventHandler(transaction_Terminated);
            m_pTransactions.Add(transaction);

            // SIP client transaction.
            if(transaction is SIP_ClientTransaction){
                InitUAC(response);

                // Attach transaction events.
                ((SIP_ClientTransaction)transaction).ResponseReceived += new SIP_ResponseReceivedEventHandler(ClientTransaction_ResponseReceived);
                ((SIP_ClientTransaction)transaction).TimedOut += new EventHandler(ClientTransaction_TimedOut);
            }
            // SIP server transaction.
            else{
                InitUAS(transaction.Request,response);

                // Attach transaction events.
                ((SIP_ServerTransaction)transaction).ResponseSent += new SIP_ResponseSentEventHandler(ServerTransaction_ResponseSent);
            }
            
            m_CreateTime = DateTime.Now;

            // Terminate early dialog after 3 min.
            if(m_DialogState == SIP_DialogState.Early){
                m_pEarlyDialogTimer = new Timer(3 * 60000);
                m_pEarlyDialogTimer.AutoReset = false;
                m_pEarlyDialogTimer.Elapsed += new ElapsedEventHandler(m_pEarlyDialogTimer_Elapsed);
                m_pEarlyDialogTimer.Enabled = true;
            }            
        }
                                
        #region mehtod Dispose

        /// <summary>
        /// Cleans up any resources being used. 
        /// Removes dialog from server dialogs, sets dialog state to terminated, ... .
        /// </summary>
        public void Dispose()
        {   
            if(m_IsDisposed){
                return;
            }
            m_IsDisposed = true;

            try{
                m_DialogState = SIP_DialogState.Terminated;
                                
                OnTerminated();

                // Clear events references.
                this.RequestReceived = null;
                this.Terminated      = null;
                this.TimedOut        = null;

                if(m_pUas2xxRetransmitTimer != null){
                    m_pUas2xxRetransmitTimer.Dispose();
                    m_pUas2xxRetransmitTimer = null;
                }
                if(m_pEarlyDialogTimer != null){
                    m_pEarlyDialogTimer.Dispose();
                    m_pEarlyDialogTimer = null;
                }
                if(m_pSessionTimer != null){
                    m_pSessionTimer.Dispose();
                    m_pSessionTimer = null;
                }
                if(m_pSessionRefreshTimer != null){
                    m_pSessionRefreshTimer.Dispose();
                    m_pSessionRefreshTimer = null;
                }

                if(m_pUac2xxWaits != null){
                    foreach(Uac2xxRetransWaitEntry entry in m_pUac2xxWaits.ToArray()){
                        entry.Dispose();
                    }
                    m_pUac2xxWaits = null;
                }

                // Log
                m_pStack.Logger.AddDebug("Dialog(id='" + this.DialogID + "') terminated.");
            }
            finally{
                // Remove dialog for dialogs collection.
                m_pStack.TransactionLayer.RemoveDialog(this);
            }
        }

        #endregion

        #region method InitUAS

        /// <summary>
        /// Initializes UAS SIP dialog.
        /// </summary>
        /// <param name="request">SIP request what caused dialog creation.</param>
        /// <param name="response">Response what caused that dialog creation.</param>
        private void InitUAS(SIP_Request request,SIP_Response response)
        {
            /* RFC 3261 12.1.1 UAS behavior.
                When a UAS responds to a request with a response that establishes a
                dialog (such as a 2xx to INVITE), the UAS MUST copy all Record-Route
                header field values from the request into the response (including the
                URIs, URI parameters, and any Record-Route header field parameters,
                whether they are known or unknown to the UAS) and MUST maintain the
                order of those values.  The UAS MUST add a Contact header field to
                the response.  The Contact header field contains an address where the
                UAS would like to be contacted for subsequent requests in the dialog
                (which includes the ACK for a 2xx response in the case of an INVITE).
                Generally, the host portion of this URI is the IP address or FQDN of
                the host.  The URI provided in the Contact header field MUST be a SIP
                or SIPS URI.  If the request that initiated the dialog contained a
                SIPS URI in the Request-URI or in the top Record-Route header field
                value, if there was any, or the Contact header field if there was no
                Record-Route header field, the Contact header field in the response
                MUST be a SIPS URI.  The URI SHOULD have global scope (that is, the
                same URI can be used in messages outside this dialog).  The same way,
                the scope of the URI in the Contact header field of the INVITE is not
                limited to this dialog either.  It can therefore be used in messages
                to the UAC even outside this dialog.

                If the request arrived over TLS, and the Request-URI contained a SIPS
                URI, the "secure" flag is set to TRUE.

                The route set MUST be set to the list of URIs in the Record-Route
                header field from the request, taken in order and preserving all URI
                parameters.  If no Record-Route header field is present in the
                request, the route set MUST be set to the empty set.  This route set,
                even if empty, overrides any pre-existing route set for future
                requests in this dialog.  The remote target MUST be set to the URI
                from the Contact header field of the request.

                The remote sequence number MUST be set to the value of the sequence
                number in the CSeq header field of the request.  The local sequence
                number MUST be empty.  The call identifier component of the dialog ID
                MUST be set to the value of the Call-ID in the request.  The local
                tag component of the dialog ID MUST be set to the tag in the To field
                in the response to the request (which always includes a tag), and the
                remote tag component of the dialog ID MUST be set to the tag from the
                From field in the request.  

                The remote URI MUST be set to the URI in the From field, and the
                local URI MUST be set to the URI in the To field.
            */

            m_Method           = request.Method;
            m_CallID           = request.CallID;
            m_LocalTag         = response.To.ToTag;
            m_RemoteTag        = request.From.Tag;
            m_LocalSequenceNo  = 0;
            m_RemoteSequenceNo = request.CSeq.SequenceNumber;
            m_LocalUri         = request.To.Address.Uri;
            m_RemoteUri        = request.From.Address.Uri;
            if(response.Contact.Count > 0){
                m_RemoteTarget = request.Contact.GetTopMostValue().Address.Uri;
            }
            // Contact header is optional for 1xx responses, for 2xx it's mandatory.
            else if(response.StatusCodeType != SIP_StatusCodeType.Provisional){
                throw new ArgumentException("Contact header is missing, it's mandatory for 2xx response !");
            }
            m_pRouteSet        = request.RecordRoute.GetAllValues();
            m_IsSecure         = false;
            m_IsServer         = true;
            if(response.StatusCodeType == SIP_StatusCodeType.Success){
                m_DialogState = SIP_DialogState.Confirmed;
            }
            else{
                m_DialogState = SIP_DialogState.Early;
            }

            m_pStack.Logger.AddDebug("Dialog(id='" + this.DialogID + "' state=" + m_DialogState + " server=" + m_IsServer + ") created.");
        }

        #endregion

        #region method InitUAC

        /// <summary>
        /// Initializes UAC SIP dialog.
        /// </summary>
        /// <param name="response">SIP response what caused dialog creation.</param>
        private void InitUAC(SIP_Response response)
        {           
            /* RFC 3261 12.1.2 UAC Behavior
                When a UAC sends a request that can establish a dialog (such as an
                INVITE) it MUST provide a SIP or SIPS URI with global scope (i.e.,
                the same SIP URI can be used in messages outside this dialog) in the
                Contact header field of the request.  If the request has a Request-
                URI or a topmost Route header field value with a SIPS URI, the
                Contact header field MUST contain a SIPS URI.
                If the request was sent over TLS, and the Request-URI contained a
                SIPS URI, the "secure" flag is set to TRUE.

                The route set MUST be set to the list of URIs in the Record-Route
                header field from the response, taken in reverse order and preserving
                all URI parameters.  If no Record-Route header field is present in
                the response, the route set MUST be set to the empty set.  This route
                set, even if empty, overrides any pre-existing route set for future
                requests in this dialog.  The remote target MUST be set to the URI
                from the Contact header field of the response.

                The local sequence number MUST be set to the value of the sequence
                number in the CSeq header field of the request.  The remote sequence
                number MUST be empty (it is established when the remote UA sends a
                request within the dialog).  The call identifier component of the
                dialog ID MUST be set to the value of the Call-ID in the request.
                The local tag component of the dialog ID MUST be set to the tag in
                the From field in the request, and the remote tag component of the
                dialog ID MUST be set to the tag in the To field of the response. 

                The remote URI MUST be set to the URI in the To field, and the local
                URI MUST be set to the URI in the From field.            
            */

            m_Method           = response.CSeq.RequestMethod.ToUpper();
            m_CallID           = response.CallID;
            m_LocalTag         = response.From.Tag;
            m_RemoteTag        = response.To.ToTag;
            m_LocalSequenceNo  = response.CSeq.SequenceNumber;
            m_RemoteSequenceNo = 0;
            m_LocalUri         = response.From.Address.Uri;
            m_RemoteUri        = response.To.Address.Uri;
            if(response.Contact.Count > 0){
                m_RemoteTarget = response.Contact.GetTopMostValue().Address.Uri;
            }
            // Contact header is optional for 1xx responses, for 2xx it's mandatory.
            else if(response.StatusCodeType != SIP_StatusCodeType.Provisional){
                throw new ArgumentException("Contact header is missing, it's mandatory for 2xx response !");
            }
            m_RemoteTarget     = response.Contact.GetTopMostValue().Address.Uri;
            m_pRouteSet        = response.RecordRoute.GetAllValues();
            m_IsSecure         = false;
            m_IsServer         = false;
            if(response.StatusCodeType == SIP_StatusCodeType.Success){
                m_DialogState = SIP_DialogState.Confirmed;
            }
            else{
                m_DialogState = SIP_DialogState.Early;
            }

            m_pStack.Logger.AddDebug("Dialog(id='" + this.DialogID + "' state=" + m_DialogState + " server=" + m_IsServer + ") created.");
        }

        #endregion


        #region Events Handling

        #region method ClientTransaction_ResponseReceived

        /// <summary>
        /// Is called when dialog client transaction has got response.
        /// </summary>
        /// <param name="e">Event data.</param>
        private void ClientTransaction_ResponseReceived(SIP_ResponseReceivedEventArgs e)
        {
            lock(this){
                // If early dialog, check if response is meant for us, this is because 
                // early dialog creator transaction may belong to another early dialog too.
                if(this.DialogState == SIP_DialogState.Early){
                    if(e.Response.To.ToTag != this.RemoteTag){
                        return;
                    }
                }

                // Send PRACK for INVITE reliable provisional responses.
                if(e.Response.CSeq.RequestMethod.ToUpper() == SIP_Methods.INVITE && e.Response.StatusCode >= 101 && e.Response.StatusCode < 200 && SIP_Utils.ContainsOptionTag(e.Response.Require.GetAllValues(),SIP_OptionTags.x100rel)){
                    /* RFC 3262 4.
                        Once a reliable provisional response is received, retransmissions of
                        that response MUST be discarded.  A response is a retransmission when
                        its dialog ID, CSeq, and RSeq match the original response.  The UAC
                        MUST maintain a sequence number that indicates the most recently
                        received in-order reliable provisional response for the initial request. 
                      
                        If the UAC receives another reliable provisional response to the same request, 
                        and its RSeq value is not one higher than the value of the sequence number, 
                        that response MUST NOT be acknowledged with a PRACK, and MUST NOT be processed 
                        further by the UAC.
                    */
                    if(e.Response.RSeq > e.ClientTransaction.RSeq){
                        e.ClientTransaction.RSeq = e.Response.RSeq;
                        SIP_ClientTransaction transaction = CreateTransaction(CreatePrack(e.Response));
                        transaction.Begin();
                    }
                }

                // Handle RFC 3262 session timer for INVITE and UPDATE.
                if(e.Response.StatusCodeType == SIP_StatusCodeType.Success && (e.ClientTransaction.Request.Method == SIP_Methods.INVITE || e.ClientTransaction.Request.Method == SIP_Methods.UPDATE)){
                    // FIX ME: What about offerless INVITE ??? 
                    m_LocalSDP = e.ClientTransaction.Request.Data;
                    UpdateSessionTimer(e.Response,true);
                }

                /* RFC 3261 12.2.1.2.
                    When a UAC receives a 2xx response to a target refresh request, it
                    MUST replace the dialog's remote target URI with the URI from the
                    Contact header field in that response, if present.
                */
                if(e.Response.StatusCodeType == SIP_StatusCodeType.Success && this.IsTargetRefreshRequest(e.ClientTransaction.Request)){
                    if(e.Response.Contact.GetTopMostValue() != null){
                        m_RemoteTarget = e.Response.Contact.GetTopMostValue().Address.Uri;
                    }
                }

                /* RFC 3261 12.2.1.2.
                    If the response for a request within a dialog is a 481 (Call/Transaction Does Not Exist) 
                    or a 408 (Request Timeout), the UAC SHOULD terminate the dialog.
                */
                if(e.Response.StatusCode == 481 || e.Response.StatusCode == 408){
                    Dispose();
                }
                /* RFC 3261 12.3.
                    Independent of the method, if a request outside of a dialog generates a non-2xx 
                    final response, any early dialogs created through provisional responses to that 
                    request are terminated.
                */
                else if(this.DialogState == SIP_DialogState.Early && e.Response.StatusCode >= 300){
                    Dispose();
                }
                else if(e.ClientTransaction.Request.Method == SIP_Methods.INVITE){
                    /* RFC 3261 13.2.2.4.
                        The UAC core MUST generate an ACK request for each 2xx received from the transaction layer. 
                        The ACK MUST contain the same credentials as the INVITE. ACK MUST be passed to the client 
                        transport every time a retransmission of the 2xx final response that triggered the ACK arrives.
                    */
                    if(e.Response.StatusCodeType == SIP_StatusCodeType.Success){
                        SIP_Request ack = this.CreateAck(e.ClientTransaction.Request);
                        try{
                            m_pStack.TransportLayer.SendRequest(ack);
                            m_pStack.Logger.AddDebug("Dialog(id='" + this.DialogID + "') ACK sent.");
                        }
                        catch(Exception x){
                            m_pStack.Logger.AddDebug("Dialog(id='" + this.DialogID + "') ACK send failed '" + x.Message + "'.");
                        }

                        /* RFC 3261 13.2.2.4.
                            We need to wait 32 seconds for 2xx restransmition and response with ACK.
                        */
                        m_pUac2xxWaits.Add(new Uac2xxRetransWaitEntry(this,e.Response,ack));                        
                    }

                    /* RFC 3261 13.2.2.4.
                        If the dialog identifier in the 2xx response matches the dialog
                        identifier of an existing dialog, the dialog MUST be transitioned to
                        the "confirmed" state, and the route set for the dialog MUST be recomputed 
                        based on the 2xx response using the procedures of Section 12.2.1.2.
                        WE DON'T DO IT, its for SIP 1.0 backward compatibility, what we don't support.
                    */
                    if(e.Response.StatusCodeType == SIP_StatusCodeType.Success && m_DialogState != SIP_DialogState.Confirmed){                        
                        m_DialogState = SIP_DialogState.Confirmed;

                        m_pStack.Logger.AddDebug("Dialog(id='" + this.DialogID + "') switched to confirmed state.");
                    }
                }
            }
        }

        #endregion

        #region method ClientTransaction_TimedOut

        /// <summary>
        /// Is called when dialog client transaction has timed out.
        /// </summary>
        /// <param name="sender">SIP client transaction.</param>
        /// <param name="e">Event data.</param>
        private void ClientTransaction_TimedOut(object sender,EventArgs e)
        {
            lock(this){
                /* RFC 3261 12.2.1.2.
                    A UAC SHOULD terminate a dialog if no response at all is received for 
                    the request (the client transaction would inform the TU about the timeout.)
                */
                Dispose();
            }
        }

        #endregion
                
        #region method ServerTransaction_ResponseSent

        /// <summary>
        /// This method is called when server transaction has sent response.
        /// </summary>
        /// <param name="e">Event data.</param>
        private void ServerTransaction_ResponseSent(SIP_ResponseSentEventArgs e)
        {
            lock(this){
                // If early dialog, check if response is meant for us, this is because 
                // early dialog creator transaction may belong to another early dialog too.
                if(this.DialogState == SIP_DialogState.Early){
                    if(e.Response.To.ToTag != this.LocalTag){
                        return;
                    }
                }

                // Handle RFC 3262 session timer for INVITE and UPDATE.
                if(e.Response.StatusCodeType == SIP_StatusCodeType.Success && (e.ServerTransaction.Method == SIP_Methods.INVITE || e.ServerTransaction.Method == SIP_Methods.UPDATE)){
                    m_LocalSDP = e.Response.Data;
                    UpdateSessionTimer(e.Response,false);
                }

                if(e.Response.CSeq.RequestMethod.ToUpper() == SIP_Methods.INVITE){
                    /* RFC 3261 13.3.1.4.
                        Once the response has been constructed, it is passed to the INVITE server transaction. 
                        Note, however, that the INVITE server transaction will be destroyed as soon as it 
                        receives this final response and passes it to the transport. Therefore, it is necessary
                        to periodically pass the response directly to the transport until the ACK arrives. 
                        The 2xx response is passed to the transport with an interval that starts at T1 seconds 
                        and doubles for each retransmission until it reaches T2 seconds (T1 and T2 are defined in
                        Section 17). Response retransmissions cease when an ACK request for the response is received. 
                        This is independent of whatever transport protocols are used to send the response.
                        If the server retransmits the 2xx response for 64*T1 seconds without receiving an ACK, 
                        the dialog is confirmed, but the session SHOULD be terminated. This is accomplished 
                        with a BYE, as described in Section 15.
                    */
                    if(e.Response.StatusCodeType == SIP_StatusCodeType.Success && m_pUas2xxRetransmitTimer == null){
                        // Store response what to retransmit.
                        m_p2xxRetransmitResponse = e.Response;
                        m_2xxRetransmitStartTime = DateTime.Now;

                        m_pUas2xxRetransmitTimer = new Timer(SIP_TimerConstants.T1);
                        m_pUas2xxRetransmitTimer.AutoReset = true;
                        m_pUas2xxRetransmitTimer.Elapsed += new ElapsedEventHandler(m_pUas2xxRetransmitTimer_Elapsed);
                        m_pUas2xxRetransmitTimer.Enabled = true;

                        m_pStack.Logger.AddDebug("Dialog(id='" + this.DialogID + "') 2xx response retransmit timer started.");
                    }
                }
            }
        }

        #endregion

        #region method transaction_Terminated

        /// <summary>
        /// Is called when SIP transaction has terminated.
        /// </summary>
        /// <param name="sender">Transaction what terminated.</param>
        /// <param name="e">Event data.</param>
        private void transaction_Terminated(object sender,EventArgs e)
        {
            m_pTransactions.Remove((SIP_Transaction)sender);
        }

        #endregion


        #region method m_pUas2xxRetransmitTimer_Elapsed

        /// <summary>
        /// This method is called when dialog must retransmit 2xx response caller.
        /// </summary>
        /// <param name="sender">Sender.</param>
        /// <param name="e">Event data.</param>
        private void m_pUas2xxRetransmitTimer_Elapsed(object sender,ElapsedEventArgs e)
        {
            /* RFC 3261 13.3.1.4.
                The 2xx response is passed to the transport with an interval that starts at T1 seconds and 
                doubles for each retransmission until it reaches T2 seconds. If the server retransmits the 
                2xx response for 64*T1 seconds without receiving an ACK, the dialog is confirmed, but the 
                session SHOULD be terminated. This is accomplished with a BYE, as described in Section 15.
            */
            lock(this){
                // If Dialog disposed same time, we need to skip code below.
                if(m_IsDisposed){
                    return;
                }

                if(m_2xxRetransmitStartTime.AddMilliseconds(64 * SIP_TimerConstants.T1) < DateTime.Now){
                    // Kill timer
                    m_pUas2xxRetransmitTimer.Dispose();
                    m_pUas2xxRetransmitTimer = null;

                    m_pStack.Logger.AddDebug("Dialog(id='" + this.DialogID + "') 2xx response retransmit timer stopped, no ACK received.");
                                     
                    m_p2xxRetransmitResponse = null;
                    m_DialogState = SIP_DialogState.Confirmed;
                    Terminate();
                }
                else{
                    m_pUas2xxRetransmitTimer.Interval = Math.Min(m_pUas2xxRetransmitTimer.Interval * 2,SIP_TimerConstants.T2);
                    
                    m_pStack.Logger.AddDebug("Dialog(id='" + this.DialogID + "') 2xx response retransmited.");

                    m_pStack.TransportLayer.SendResponse(null,m_p2xxRetransmitResponse);
                }
            }
        }

        #endregion

        #region method m_pEarlyDialogTimer_Elapsed

        /// <summary>
        /// This method is called when early dialog timeout reached.
        /// </summary>
        /// <param name="sender">Sender.</param>
        /// <param name="e">Event data.</param>
        private void m_pEarlyDialogTimer_Elapsed(object sender,ElapsedEventArgs e)
        {
            if(this.DialogState == SIP_DialogState.Early){
                Dispose();
            }
        }

        #endregion

        #region method m_pSessionTimer_Elapsed

        /// <summary>
        /// This method is called when session times out.
        /// </summary>
        /// <param name="sender">Sender.</param>
        /// <param name="e">Event data.</param>
        private void m_pSessionTimer_Elapsed(object sender,ElapsedEventArgs e)
        {
            Dispose();
        }

        #endregion

        #region method m_pSessionRefreshTimer_Elapsed

        /// <summary>
        /// This method is called when we need to refresh session by re-INVITE or UPDATE request.
        /// </summary>
        /// <param name="sender">Sender.</param>
        /// <param name="e">Event data.</param>
        private void m_pSessionRefreshTimer_Elapsed(object sender,ElapsedEventArgs e)
        {
            // Log
            m_pStack.Logger.AddDebug("Dialog(id='" + this.DialogID + "' state=" + m_DialogState + " server=" + m_IsServer + ") session refresh timer triggered, updating session.");
                        
            // Create session refresh re-INVITE.
            SIP_Request reInvite = CreateRequest(SIP_Methods.INVITE);
            reInvite.SessionExpires.Refresher = "uas";
            reInvite.ContentType = "application/sdp";
            reInvite.Data = m_LocalSDP;

            SIP_ClientTransaction transaction = CreateTransaction(reInvite);
            transaction.Begin();
        }

        #endregion

        #endregion


        #region method CreateRequest

        /// <summary>
        /// Creates new SIP request using this dialog info.
        /// </summary>
        /// <param name="method">SIP request method.</param>
        /// <returns>Returns created SIP request.</returns>
        /// <exception cref="ArgumentException">Is raised when any of the arguments have invalid value.</exception>
        /// <exception cref="InvalidOperationException">Is raised when when dialog is in early or terminated state.</exception>
        public SIP_Request CreateRequest(string method)
        {            
            /* RFC 3261 12.2.1.1 Generating the Request.
                The URI in the To field of the request MUST be set to the remote URI
                from the dialog state.  The tag in the To header field of the request
                MUST be set to the remote tag of the dialog ID.  The From URI of the
                request MUST be set to the local URI from the dialog state.  The tag
                in the From header field of the request MUST be set to the local tag
                of the dialog ID.  If the value of the remote or local tags is null,
                the tag parameter MUST be omitted from the To or From header fields,
                respectively.
                The Call-ID of the request MUST be set to the Call-ID of the dialog.
                Requests within a dialog MUST contain strictly monotonically
                increasing and contiguous CSeq sequence numbers (increasing-by-one).
             
                The UAC uses the remote target and route set to build the Request-URI
                and Route header field of the request.   
              
                A UAC SHOULD include a Contact header field in any target refresh
                requests within a dialog, and unless there is a need to change it,
                the URI SHOULD be the same as used in previous requests within the
                dialog.  If the "secure" flag is true, that URI MUST be a SIPS URI.
                As discussed in Section 12.2.2, a Contact header field in a target
                refresh request updates the remote target URI.  This allows a UA to
                provide a new contact address, should its address change during the
                duration of the dialog.
            */

            if(string.IsNullOrEmpty(method)){
                throw new ArgumentException("Argument 'method' value can't be null or empty !");
            }

            // ACK, BYE must be available for Early.
            //if(m_DialogState == SIP_DialogState.Early || m_DialogState == SIP_DialogState.Terminated){
            //    throw new InvalidOperationException("Invalid dialog state for CreateRequest method !");
            //}

            SIP_Request request = new SIP_Request();
            request.Method = method;           
            request.To = new SIP_t_To(this.RemoteUri);
            request.To.ToTag = this.RemoteTag;
            request.From = new SIP_t_From(this.LocalUri);
            request.From.Tag = this.LocalTag;
            request.CallID = this.CallID;   
            // ACK won't affect CSeq.
            if(method != SIP_Methods.ACK){
                request.CSeq = new SIP_t_CSeq(++m_LocalSequenceNo,method);
            }
                        
            // If the route set is empty, the UAC MUST place the remote target URI into the Request-URI.
            if(this.Routes.Length == 0){
                request.Uri = this.RemoteTarget;
            }
            else{
                /*
                    If the route set is not empty, and the first URI in the route set
                    contains the lr parameter (see Section 19.1.1), the UAC MUST place
                    the remote target URI into the Request-URI and MUST include a Route
                    header field containing the route set values in order, including all
                    parameters.
                  
                    If the route set is not empty, and its first URI does not contain the
                    lr parameter, the UAC MUST place the first URI from the route set
                    into the Request-URI, stripping any parameters that are not allowed
                    in a Request-URI.  The UAC MUST add a Route header field containing
                    the remainder of the route set values in order, including all
                    parameters.  The UAC MUST then place the remote target URI into the
                    Route header field as the last value.

                    For example, if the remote target is sip:user@remoteua and the route
                    set contains:
                        <sip:proxy1>,<sip:proxy2>,<sip:proxy3;lr>,<sip:proxy4>

                    The request will be formed with the following Request-URI and Route
                    header field:
                        METHOD sip:proxy1
                        Route: <sip:proxy2>,<sip:proxy3;lr>,<sip:proxy4>,<sip:user@remoteua>
                */

                SIP_Uri route = SIP_Uri.Parse(this.Routes[0].Address.Uri);
                if(route.Param_Lr){
                    request.Uri = this.RemoteTarget;
                    for(int i=0;i<this.Routes.Length;i++){
                        request.Route.Add(this.Routes[i].ToStringValue());
                    }
                }
                else{
                    request.Uri = SIP_Utils.UriToRequestUri(this.Routes[0].Address.Uri);
                    for(int i=1;i<this.Routes.Length;i++){
                        request.Route.Add(this.Routes[i].ToStringValue());
                    }
                }
            }

            if(!this.IsSecure){
                request.Contact.Add("<sip:" + SIP_Uri.Parse(request.From.Address.Uri).User + "@" + m_pStack.GetContactHost(!IsSecure) + ">");
            }
            else{
                request.Contact.Add("<sips:" + SIP_Uri.Parse(request.From.Address.Uri).User + "@" + m_pStack.GetContactHost(!IsSecure) + ">");
            }

            // Accept to non ACK,BYE request.
            if(method != SIP_Methods.ACK && method != SIP_Methods.BYE){
                request.Allow.Add("INVITE,ACK,OPTIONS,CANCEL,BYE,PRACK");
            }
            // Supported to non ACK request. 
            if(method != SIP_Methods.ACK){
                request.Supported.Add("100rel,timer");
            }
            // Max-Forwards to any request.
            request.MaxForwards = 70;

            // RFC 4028 7.4. For re-INVITE and UPDATE we need to add Session-Expires and Min-SE: headers.
            if(method == SIP_Methods.INVITE || method == SIP_Methods.UPDATE){
                request.SessionExpires = new SIP_t_SessionExpires(m_pStack.SessionExpries,"uac");
                request.MinSE = new SIP_t_MinSE(m_pStack.MinimumSessionExpries);
            }

            return request;
        }

        #endregion
        
        // Do we need it ?
        //public void CreateResponse(SIP_Request request)
        //{
        //}
         
        #region method Terminate

        /// <summary>
        /// Terminates SIP dialog, sends BYE if needed.
        /// </summary>
        public void Terminate()
        {
            /* RFC 3261 15.
                The caller's UA MAY send a BYE for either confirmed or early dialogs, and the callee's 
                UA MAY send a BYE on confirmed dialogs, but MUST NOT send a BYE on early dialogs.
             
             TODO:
                However, the callee's UA MUST NOT send a BYE on a confirmed dialog
                until it has received an ACK for its 2xx response or until the server
                transaction times out.
                This is because, otherwise we may have race condition of sen 2xx response and BYE.
                Does this matter, we may just skip error response ?
            */ 

            if(this.DialogState == SIP_DialogState.Terminated){
                return;
            }
                  
            //if(this.DialogState == SIP_DialogState.Confirmed || (this.DialogState == SIP_DialogState.Early && !this.IsServer)){                
                SIP_Request byeRequest = CreateRequest(SIP_Methods.BYE);
                SIP_ClientTransaction transaction = m_pStack.TransactionLayer.CreateClientTransaction(byeRequest,new SIP_Target(SIP_Uri.Parse(m_RemoteTarget)),true);
                transaction.Begin();

                Dispose();
            //}
        }

        #endregion

        #region method CreateTransaction

        /// <summary>
        /// Creates new client transaction to this dialog.
        /// NOTE: There can be only 1 INVITE transaction at time, so this method throws exception if 2 
        /// INVITE transaction tried to create.
        /// </summary>
        /// <param name="request">SIP request.</param>
        /// <returns>Returns created client transaction.</returns>
        /// <exception cref="ArgumentNullException">Is raised when request argument is null.</exception>
        /// <exception cref="InvalidOperationException">Is raised when there is pending INVITE transaction and new one tried to create.</exception>
        public SIP_ClientTransaction CreateTransaction(SIP_Request request)
        {
            if(request == null){
                throw new ArgumentNullException("request");
            }
            if(request.Method == SIP_Methods.INVITE && HasPendingInvite()){
                throw new InvalidOperationException("There is pending INVITE transaction, only 1 INVITE transaction allowed at time !");
            }

            SIP_ClientTransaction transaction = m_pStack.TransactionLayer.CreateClientTransaction(request,new SIP_Target(SIP_Uri.Parse(m_RemoteTarget)),true);
            transaction.ResponseReceived += new SIP_ResponseReceivedEventHandler(ClientTransaction_ResponseReceived);
            transaction.TimedOut += new EventHandler(ClientTransaction_TimedOut);
            transaction.Terminated += new EventHandler(transaction_Terminated);
            m_pTransactions.Add(transaction);

            return transaction;
        }        
                                
        #endregion
        
        #region method HasPendingInvite

        /// <summary>
        /// Gets if dialog has pending INVITE.
        /// </summary>
        /// <returns>Returns true if there is pending INVITE, otherwise false.</returns>
        public bool HasPendingInvite()
        {
            if(GetPendingInviteTransaction() != null || m_p2xxRetransmitResponse != null){
                return true;
            }
            else{
                return false;
            }
        }

        #endregion
                

        #region method ProcessRequest

        /// <summary>
        /// Processes this request on this dialog. Dialog will update it's state as needed.
        /// This method is always called from transport layer.
        /// </summary>
        /// <param name="request">SIP request.</param>
        internal void ProcessRequest(SIP_Request request)
        {          
            lock(this){
                // This dialog is terminated, so skip all requests. Normally this never happens.
                if(m_DialogState == SIP_DialogState.Terminated){
                    return;
                }

                m_pStack.Logger.AddDebug("Dialog(id='" + this.DialogID + "') got request: " + request.Method);
                                
                if(request.Method == SIP_Methods.BYE){
                    SIP_ServerTransaction byeTransaction = m_pStack.TransactionLayer.CreateServerTransaction(request);
                    byeTransaction.SendResponse(request.CreateResponse(SIP_ResponseCodes.x200_Ok));

                    // Set dialog to terminated state, otherwise Dispose sends BYE.
                    m_DialogState = SIP_DialogState.Terminated;
                    Dispose();
                }
                // RFC 3261 13.1.4. If UAS dialog gets ACK, response retransmissions cease and dialog. 
                // switch to Confirmed state.
                else if(request.Method == SIP_Methods.ACK){
                    if(m_pUas2xxRetransmitTimer != null){
                        m_pUas2xxRetransmitTimer.Dispose();
                        m_pUas2xxRetransmitTimer = null;

                        m_pStack.Logger.AddDebug("Dialog(id='" + this.DialogID + "') 2xx response retransmit timer stoped, got ACK.");
                    }
                                        
                    m_p2xxRetransmitResponse = null;
                    m_DialogState = SIP_DialogState.Confirmed;
                }
                else if(request.Method == SIP_Methods.PRACK){
                    SIP_ServerTransaction transaction = m_pStack.TransactionLayer.CreateServerTransaction(request);

                    /* RFC 3262 3.
                        If a PRACK request is received by the UA core that does not match any
                        unacknowledged reliable provisional response, the UAS MUST respond to
                        the PRACK with a 481 response. If the PRACK does match an unacknowledged 
                        reliable provisional response, it MUST be responded to with a 2xx response.
                    */
                    bool isHandled = false;
                    SIP_Transaction peindingInvite = this.GetPendingInviteTransaction();
                    if(peindingInvite != null && peindingInvite is SIP_ServerTransaction){
                        isHandled = ((SIP_ServerTransaction)peindingInvite).ProcessPRACK(request);
                    }                    
                    if(!isHandled){
                        transaction.SendResponse(request.CreateResponse(SIP_ResponseCodes.x481_Call_Transaction_Does_Not_Exist));
                    }
                    else{
                        transaction.SendResponse(request.CreateResponse(SIP_ResponseCodes.x200_Ok));
                    }
                }
                else{
                    // Create new transaction for request.
                    SIP_ServerTransaction transaction = m_pStack.TransactionLayer.CreateServerTransaction(request);
                   
                    /* RFC 3261 14.2.
                        A UAS that receives a second INVITE before it sends the final
                        response to a first INVITE with a lower CSeq sequence number on the
                        same dialog MUST return a 500 (Server Internal Error) response to the
                        second INVITE and MUST include a Retry-After header field with a
                        randomly chosen value of between 0 and 10 seconds.

                        A UAS that receives an INVITE on a dialog while an INVITE it had sent
                        on that dialog is in progress MUST return a 491 (Request Pending)
                        response to the received INVITE.
                    */                
                    if(request.Method == SIP_Methods.INVITE){
                        SIP_Transaction inviteTransaction = GetPendingInviteTransaction();
                        if(inviteTransaction != null){
                            lock(inviteTransaction){
                                if(request.CSeq.SequenceNumber < inviteTransaction.Request.CSeq.SequenceNumber && !inviteTransaction.IsDisposed){
                                    SIP_Response response = request.CreateResponse(SIP_ResponseCodes.x500_Server_Internal_Error + ": CSeq number out of order !");
                                    response.RetryAfter = new SIP_t_RetryAfter("3");
                                    transaction.SendResponse(response);
                                    return;
                                }
                            }
                        }

                        if(this.HasPendingInvite()){
                            transaction.SendResponse(request.CreateResponse(SIP_ResponseCodes.x491_Request_Pending));
                            return;
                        }
                    }
                    /* RFC 3311 5.2.
                        A UAS that receives an UPDATE before it has generated a final
                        response to a previous UPDATE on the same dialog MUST return a 500
                        response to the new UPDATE, and MUST include a Retry-After header
                        field with a randomly chosen value between 0 and 10 seconds.
                    */
                    else if(request.Method == SIP_Methods.UPDATE){
                        foreach(SIP_Transaction t in m_pTransactions.ToArray()){
                            try{
                                if(t.Method == SIP_Methods.UPDATE && transaction.GetFinalResponse() == null){
                                    SIP_Response response = request.CreateResponse(SIP_ResponseCodes.x500_Server_Internal_Error + ": CSeq number out of order !");
                                    response.RetryAfter = new SIP_t_RetryAfter("3");
                                    transaction.SendResponse(response);
                                    return;
                                }
                            }
                            catch(ObjectDisposedException x){
                                // Transaction has terminated(disposed), skip it.
                                string dummy = x.Message;
                            }
                        }
                    }

                    // Add transaction to dialog transactions collection and attach transaction events.
                    // NOTE: We may not do this before, otherwise if INVITE we can't make pending check !
                    m_pTransactions.Add(transaction);                    
                    transaction.ResponseSent += new SIP_ResponseSentEventHandler(ServerTransaction_ResponseSent);
                    transaction.Terminated += new EventHandler(transaction_Terminated);

                    /* RFC 3261 12.2.2.
                        If the sequence number of the request is lower than the remote sequence number, 
                        the request is out of order and MUST be rejected with a 500 (Server Internal Error) response.
                    
                        If the sequence number of the request is greater, the UAS MUST then set the remote 
                        sequence number to the value of the sequence number in the CSeq header field value 
                        in the request.
                    */
                    if(request.CSeq.SequenceNumber <= m_RemoteSequenceNo){
                        transaction.SendResponse(request.CreateResponse(SIP_ResponseCodes.x500_Server_Internal_Error + ": CSeq number out of order !"));
                        return;
                    }
                    else{
                        m_RemoteSequenceNo = request.CSeq.SequenceNumber;
                    }
                                        
                    // Raise RequestReceived event.
                    OnRequestReceived(request,transaction);
                }
            }
        }

        #endregion

        #region method ProcessResponse

        /// <summary>
        /// Processes this response through this dialog. Dialog will update it's state as needed.
        /// This method is always called from transport layer for response what won't match to any transaction, but to this dialog.
        /// </summary>
        /// <param name="response">SIP response.</param>
        internal void ProcessResponse(SIP_Response response)
        {   
            // Response types:
            //  *) INVITE retransmited 2xx (need to send ACK).
            //  *) Stray response

            lock(this){
                // This dialog is terminated, so skip all responses. Normally this never happens.
                if(m_DialogState == SIP_DialogState.Terminated){
                    return;
                }

                m_pStack.Logger.AddDebug("Dialog(id='" + this.DialogID + "') got response: " + response.StatusCode_ReasonPhrase);

                if(response.CSeq.RequestMethod.ToUpper() == SIP_Methods.INVITE){
                    /* RFC 3261 13.2.2.4.
                        The UAC core MUST generate an ACK request for each 2xx received from the transaction layer. 
                        The ACK MUST contain the same credentials as the INVITE. ACK MUST be passed to the client 
                        transport every time a retransmission of the 2xx final response that triggered the ACK arrives.
                    */
                    if(response.StatusCodeType == SIP_StatusCodeType.Success){
                        // Map response to ACK.
                        lock(m_pUac2xxWaits){
                            bool found = false;
                            string responseID = response.Via.GetTopMostValue().Branch + "-" + response.CSeq.RequestMethod;
                            foreach(Uac2xxRetransWaitEntry entry in m_pUac2xxWaits){
                                if(responseID == entry.x2xxResponse.Via.GetTopMostValue().Branch + "-" + entry.x2xxResponse.CSeq.RequestMethod){
                                    found = true;
                                    try{
                                        m_pStack.TransportLayer.SendRequest(entry.Ack);

                                        m_pStack.Logger.AddDebug("Dialog(id='" + this.DialogID + "') ACK sent for retransmited 2xx response.");
                                    }                                    
                                    catch(Exception x){
                                        m_pStack.Logger.AddDebug("Dialog(id='" + this.DialogID + "') ACK send for retransmited 2xx response failed '" + x.Message + "'.");
                                    }
                                    break;
                                }
                            }

                            if(!found){
                                m_pStack.Logger.AddDebug("Dialog(id='" + this.DialogID + "') stray 2xx response, no matching ACK found.");
                            }
                        }                        
                    }
                }                
            }
        }

        #endregion

        #region method IsTargetRefreshRequest

        /// <summary>
        /// Gets if specified SIP request is target refresh request.
        /// Basically that mean if dialog remote URI must be updated.
        /// </summary>
        /// <param name="request">SIP request.</param>
        /// <returns>Returns true if specified request is target refresh request.</returns>
        private bool IsTargetRefreshRequest(SIP_Request request)
        {           
            // re-INVITE
            if(request.Method == SIP_Methods.INVITE){
                return true;
            }
            // RFC 3311 5.1. UPDATE is a target refresh request.
            else if(request.Method == SIP_Methods.UPDATE){
                return true;
            }

            return false;
        }

        #endregion
        
        #region method CreateAck

        /// <summary>
        /// Creates ACK for active INVITE transaction.
        /// </summary>
        /// <param name="request">INVITE request for what to create ACK request.</param>
        /// <returns>Returns created ACK request.</returns>
        private SIP_Request CreateAck(SIP_Request request)
        {
            /* RFC 3261 13.2.2.4.
                The header fields of the ACK are constructed
                in the same way as for any request sent within a dialog (see Section 12) with the 
                exception of the CSeq and the header fields related to authentication. The sequence 
                number of the CSeq header field MUST be the same as the INVITE being acknowledged, 
                but the CSeq method MUST be ACK. The ACK MUST contain the same credentials as the INVITE.
                
                ACK must have same branch.
            */
                        
            SIP_Request ackRequest = CreateRequest(SIP_Methods.ACK);
            ackRequest.Via.AddToTop(request.Via.GetTopMostValue().ToStringValue());
            ackRequest.CSeq = new SIP_t_CSeq(request.CSeq.SequenceNumber,SIP_Methods.ACK);
            // Authorization
            foreach(SIP_HeaderField h in request.Authorization.HeaderFields){
                ackRequest.Authorization.Add(h.Value);
            }
            // Proxy-Authorization 
            foreach(SIP_HeaderField h in request.ProxyAuthorization.HeaderFields){
                ackRequest.Authorization.Add(h.Value);
            }

            return ackRequest;        
        }

        #endregion

        #region method CreatePrack

        /// <summary>
        /// Creates PRACK request for INVITE reliable provisional response.
        /// </summary>
        /// <param name="response">INVITE reliable provisional response(101 > 199).</param>
        /// <returns>Returns PRACK created request.</returns>
        private SIP_Request CreatePrack(SIP_Response response)
        {
            SIP_Request prack = CreateRequest(SIP_Methods.PRACK);            
            prack.RAck = new SIP_t_RAck(response.RSeq,response.CSeq.SequenceNumber,response.CSeq.RequestMethod);

            return prack;
        }

        #endregion

        #region method GetPendingInviteTransaction

        /// <summary>
        /// Gets pending INVITE transaction.
        /// </summary>
        /// <returns>Returns pending INVITE transaction or null if no pending INVITE transaction.</returns>
        private SIP_Transaction GetPendingInviteTransaction()
        {
            lock(m_pTransactions){
                foreach(SIP_Transaction transaction in m_pTransactions){
                    try{
                        if(transaction.TransactionState != SIP_TransactionState.Terminated && transaction.Method == SIP_Methods.INVITE){
                            return transaction;
                        }
                    }
                    catch{
                        // We can get exception, if transaction is disposing, so just skip them.
                    }
                }
            }

            return null;
        }

        #endregion

        #region method UpdateSessionTimer

        /// <summary>
        /// Updates session timer.
        /// </summary>
        /// <param name="response">INVITE or UPDATE response.</param>
        /// <param name="uac_uas">Specifies is dialog is currently in uac or uas mode.</param>
        private void UpdateSessionTimer(SIP_Response response,bool uac_uas)
        {
            int    expires   = m_pStack.SessionExpries;
            string refresher = null;
            if(response.SessionExpires != null){
                expires = response.SessionExpires.Expires;
                refresher = response.SessionExpires.Refresher.ToLower();                
            }

            if(m_pSessionTimer != null){
                m_pSessionTimer.Dispose();
            }
            m_pSessionTimer = new Timer(expires * 1000);
            m_pSessionTimer.AutoReset = false;
            m_pSessionTimer.Elapsed += new ElapsedEventHandler(m_pSessionTimer_Elapsed);
            m_pSessionTimer.Enabled = true;

            // Log
            m_pStack.Logger.AddDebug("Dialog(id='" + this.DialogID + "' state=" + m_DialogState + " server=" + m_IsServer + ") session timer started, will trigger after " + expires + " seconds.");
            
            bool weRefresh = true;
            if(refresher == null){
                weRefresh = true;
            }
            else if(uac_uas){
                if(refresher == "uac"){
                    weRefresh = true;
                }
            }
            else{
                if(refresher == "uas"){
                    weRefresh = true;
                }
            }

            if(weRefresh){
                if(m_pSessionRefreshTimer != null){
                    m_pSessionRefreshTimer.Dispose();
                }
                m_pSessionRefreshTimer = new Timer((expires * 1000) / 2);
                m_pSessionRefreshTimer.AutoReset = true;
                m_pSessionRefreshTimer.Elapsed += new ElapsedEventHandler(m_pSessionRefreshTimer_Elapsed);
                m_pSessionRefreshTimer.Enabled = true;

                // Log
                m_pStack.Logger.AddDebug("Dialog(id='" + this.DialogID + "' state=" + m_DialogState + " server=" + m_IsServer + ") session refresh timer started, will trigger after " + (expires / 2) + " seconds.");
            }
        }

        #endregion


        #region Properties Implementation

        /// <summary>
        /// Gets SIP METHOD that caused dialog creation.
        /// </summary>
        public string Method
        {
            get{ return m_Method; }
        }

        /// <summary>
        /// Gets dialog ID. Dialog ID is composed from CallID + '-' + Local Tag + '-' + Remote Tag.
        /// </summary>
        public string DialogID
        {
            get{ return this.CallID + "-" + this.LocalTag + "-" + this.RemoteTag; }
        }

        /// <summary>
        /// Gets dialog state.
        /// </summary>
        public SIP_DialogState DialogState
        {
            get{ return m_DialogState; }
        }

        /// <summary>
        /// Gets call ID.
        /// </summary>
        public string CallID
        {
            get{ return m_CallID; }
        }

        /// <summary>
        /// Gets From: header field tag parameter value.
        /// </summary>
        public string LocalTag
        {
            get{ return m_LocalTag; }
        }

        /// <summary>
        /// Gets To: header field tag parameter value.
        /// </summary>
        public string RemoteTag
        {
            get{ return m_RemoteTag; }
        }

        /// <summary>
        /// Gets local command sequence(CSeq) number.
        /// </summary>
        public int LocalSequenceNo
        {
            get{ return m_LocalSequenceNo; }
        }

        /// <summary>
        /// Gets remote command sequence(CSeq) number.
        /// </summary>
        public int RemoteSequenceNo
        {
            get{ return m_RemoteSequenceNo; }
        }

        /// <summary>
        /// Gets local UAC From: header field URI.
        /// </summary>
        public string LocalUri
        {
            get{ return m_LocalUri; }
        }

        /// <summary>
        /// Gets remote UAC From: header field URI.
        /// </summary>
        public string RemoteUri
        {
            get{ return m_RemoteUri; }
        }

        /// <summary>
        /// Gets remote UAC Contact: header field URI.
        /// </summary>
        public string RemoteTarget
        {
            get{ return m_RemoteTarget; }
        }

        /// <summary>
        /// Gets the list of servers that need to be traversed to send a request to the peer.
        /// </summary>
        public SIP_t_AddressParam[] Routes
        {
            get{ return m_pRouteSet; }
        }

        /// <summary>
        /// Gets if request was done over TLS.
        /// </summary>
        public bool IsSecure
        {
            get{ return m_IsSecure; }
        }
                
        /// <summary>
        /// Gets or sets user data.
        /// </summary>
        public object Tag
        {
            get{ return m_pTag; }

            set{ m_pTag = value; }
        }

        /// <summary>
        /// Gets dialog creation time.
        /// </summary>
        public DateTime CreateTime
        {
            get{ return m_CreateTime; }
        }

        /// <summary>
        /// Gets this dialog active transaction.
        /// </summary>
        public SIP_Transaction[] Transactions
        {
            get{ return m_pTransactions.ToArray(); }
        }

        /// <summary>
        /// Gets SIP stack which is used by this dialog.
        /// </summary>
        public SIP_Stack Stack
        {
            get{ return m_pStack; }
        }

        #endregion

        #region Events Implementation

        /// <summary>
        /// Is raised when dialog is terminated.
        /// </summary>
        public event EventHandler Terminated = null;

        /// <summary>
        /// Raises Terminated event.
        /// </summary>
        protected void OnTerminated()
        {
            if(this.Terminated != null){
                this.Terminated(this,new EventArgs());
            }
        }

        /// <summary>
        /// Is raised when dialog times out.
        /// </summary>
        public event EventHandler TimedOut = null;

        /// <summary>
        /// Raises TimedOut event.
        /// </summary>
        protected void OnTimedOut()
        {
            if(this.TimedOut != null){
                this.TimedOut(this,new EventArgs());
            }
        }

        /// <summary>
        /// Is raised when dialog receives new request.
        /// </summary>
        public event SIP_RequestReceivedEventHandler RequestReceived = null;

        /// <summary>
        /// Raises <b>RequestReceived</b> event.
        /// </summary>
        /// <param name="request">Request which was received.</param>
        /// <param name="transaction">Server transaction that must be used to send response back to request.</param>
        protected void OnRequestReceived(SIP_Request request,SIP_ServerTransaction transaction)
        {            
            if(this.RequestReceived != null){
                this.RequestReceived(new SIP_RequestReceivedEventArgs(m_pStack,request,this,transaction));
            }
        }

        #endregion

    }
}
