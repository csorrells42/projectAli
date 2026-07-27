# AliSecurityModule

The sole pre-Ali authorization policy. It joins evidence for the same utterance
and opens STT only for PTT, authenticated login, authorized visual attention,
or a dynamic wake phrase plus a positively recognized speaker matching the
authorized target. The wake phrase bypasses only the attention requirement;
it never bypasses speaker recognition or authorization. Login is explicitly
unavailable in Avatar Builder. Voice alone never authorizes.
