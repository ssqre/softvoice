using System;
using System.Collections.Generic;
using System.Text;

namespace LumiSoft.Net.SIP.Stack
{
    /// <summary>
    /// This class provides data for SIP_Core.OnResponseReceived method.
    /// </summary>
    public class SIP_ResponseReceivedEventArgs
    {
        private SIP_Stack             m_pStack       = null;
        private SIP_Response          m_pResponse    = null;
        private SIP_ClientTransaction m_pTransaction = null;

        /// <summary>
        /// Default constructor.
        /// </summary>
        /// <param name="stack">Reference to SIP stack.</param>
        /// <param name="transaction">Client transaction what response it is. This value can be null if no matching client response.</param>
        /// <param name="response">Received response.</param>
        internal SIP_ResponseReceivedEventArgs(SIP_Stack stack,SIP_ClientTransaction transaction,SIP_Response response)
        {
            m_pStack       = stack;
            m_pResponse    = response;
            m_pTransaction = transaction;
        }


        #region Properties Implementation

        /// <summary>
        /// Gets response received by SIP stack.
        /// </summary>
        public SIP_Response Response
        {
            get{ return m_pResponse; }
        }

        /// <summary>
        /// Gets client transaction which response it is. This value is null if no matching client transaction.
        /// If this core is staless proxy then it's allowed, otherwise core MUST discard that response.
        /// </summary>
        public SIP_ClientTransaction ClientTransaction
        {
            get{ return m_pTransaction; }
        }

        /// <summary>
        /// Gets SIP dialog where Response belongs to. Returns null if Response doesn't belong any dialog.
        /// </summary>
        public SIP_Dialog Dialog
        {
            get{ throw new NotImplementedException(); }
        }

        #endregion

    }
}
