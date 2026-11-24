.CODE

EXTERN pfn_curl_easy_duphandle:QWORD
EXTERN pfn_curl_easy_escape:QWORD
EXTERN pfn_curl_easy_getinfo:QWORD
EXTERN pfn_curl_easy_header:QWORD
EXTERN pfn_curl_easy_init:QWORD
EXTERN pfn_curl_easy_nextheader:QWORD
EXTERN pfn_curl_easy_option_by_id:QWORD
EXTERN pfn_curl_easy_option_by_name:QWORD
EXTERN pfn_curl_easy_option_next:QWORD
EXTERN pfn_curl_easy_pause:QWORD
EXTERN pfn_curl_easy_perform:QWORD
EXTERN pfn_curl_easy_recv:QWORD
EXTERN pfn_curl_easy_reset:QWORD
EXTERN pfn_curl_easy_send:QWORD
EXTERN pfn_curl_easy_setopt:QWORD
EXTERN pfn_curl_easy_ssls_export:QWORD
EXTERN pfn_curl_easy_ssls_import:QWORD
EXTERN pfn_curl_easy_strerror:QWORD
EXTERN pfn_curl_easy_unescape:QWORD
EXTERN pfn_curl_easy_upkeep:QWORD
EXTERN pfn_curl_escape:QWORD
EXTERN pfn_curl_formadd:QWORD
EXTERN pfn_curl_formfree:QWORD
EXTERN pfn_curl_formget:QWORD
EXTERN pfn_curl_free:QWORD
EXTERN pfn_curl_getdate:QWORD
EXTERN pfn_curl_getenv:QWORD
EXTERN pfn_curl_global_init_mem:QWORD
EXTERN pfn_curl_global_sslset:QWORD
EXTERN pfn_curl_global_trace:QWORD
EXTERN pfn_curl_maprintf:QWORD
EXTERN pfn_curl_mfprintf:QWORD
EXTERN pfn_curl_mime_addpart:QWORD
EXTERN pfn_curl_mime_data:QWORD
EXTERN pfn_curl_mime_data_cb:QWORD
EXTERN pfn_curl_mime_encoder:QWORD
EXTERN pfn_curl_mime_filedata:QWORD
EXTERN pfn_curl_mime_filename:QWORD
EXTERN pfn_curl_mime_free:QWORD
EXTERN pfn_curl_mime_headers:QWORD
EXTERN pfn_curl_mime_init:QWORD
EXTERN pfn_curl_mime_name:QWORD
EXTERN pfn_curl_mime_subparts:QWORD
EXTERN pfn_curl_mime_type:QWORD
EXTERN pfn_curl_mprintf:QWORD
EXTERN pfn_curl_msnprintf:QWORD
EXTERN pfn_curl_msprintf:QWORD
EXTERN pfn_curl_multi_add_handle:QWORD
EXTERN pfn_curl_multi_assign:QWORD
EXTERN pfn_curl_multi_cleanup:QWORD
EXTERN pfn_curl_multi_fdset:QWORD
EXTERN pfn_curl_multi_get_handles:QWORD
EXTERN pfn_curl_multi_get_offt:QWORD
EXTERN pfn_curl_multi_info_read:QWORD
EXTERN pfn_curl_multi_init:QWORD
EXTERN pfn_curl_multi_notify_disable:QWORD
EXTERN pfn_curl_multi_notify_enable:QWORD
EXTERN pfn_curl_multi_perform:QWORD
EXTERN pfn_curl_multi_poll:QWORD
EXTERN pfn_curl_multi_remove_handle:QWORD
EXTERN pfn_curl_multi_setopt:QWORD
EXTERN pfn_curl_multi_socket:QWORD
EXTERN pfn_curl_multi_socket_action:QWORD
EXTERN pfn_curl_multi_socket_all:QWORD
EXTERN pfn_curl_multi_strerror:QWORD
EXTERN pfn_curl_multi_timeout:QWORD
EXTERN pfn_curl_multi_wait:QWORD
EXTERN pfn_curl_multi_waitfds:QWORD
EXTERN pfn_curl_multi_wakeup:QWORD
EXTERN pfn_curl_mvaprintf:QWORD
EXTERN pfn_curl_mvfprintf:QWORD
EXTERN pfn_curl_mvprintf:QWORD
EXTERN pfn_curl_mvsnprintf:QWORD
EXTERN pfn_curl_mvsprintf:QWORD
EXTERN pfn_curl_pushheader_byname:QWORD
EXTERN pfn_curl_pushheader_bynum:QWORD
EXTERN pfn_curl_share_cleanup:QWORD
EXTERN pfn_curl_share_init:QWORD
EXTERN pfn_curl_share_setopt:QWORD
EXTERN pfn_curl_share_strerror:QWORD
EXTERN pfn_curl_slist_append:QWORD
EXTERN pfn_curl_slist_free_all:QWORD
EXTERN pfn_curl_strequal:QWORD
EXTERN pfn_curl_strnequal:QWORD
EXTERN pfn_curl_unescape:QWORD
EXTERN pfn_curl_url:QWORD
EXTERN pfn_curl_url_cleanup:QWORD
EXTERN pfn_curl_url_dup:QWORD
EXTERN pfn_curl_url_get:QWORD
EXTERN pfn_curl_url_set:QWORD
EXTERN pfn_curl_url_strerror:QWORD
EXTERN pfn_curl_version:QWORD
EXTERN pfn_curl_version_info:QWORD
EXTERN pfn_curl_ws_meta:QWORD
EXTERN pfn_curl_ws_recv:QWORD
EXTERN pfn_curl_ws_send:QWORD
EXTERN pfn_curl_ws_start_frame:QWORD

PUBLIC curl_easy_duphandle
curl_easy_duphandle PROC
    jmp qword ptr [pfn_curl_easy_duphandle]
curl_easy_duphandle ENDP

PUBLIC curl_easy_escape
curl_easy_escape PROC
    jmp qword ptr [pfn_curl_easy_escape]
curl_easy_escape ENDP

PUBLIC curl_easy_getinfo
curl_easy_getinfo PROC
    jmp qword ptr [pfn_curl_easy_getinfo]
curl_easy_getinfo ENDP

PUBLIC curl_easy_header
curl_easy_header PROC
    jmp qword ptr [pfn_curl_easy_header]
curl_easy_header ENDP

PUBLIC curl_easy_init
curl_easy_init PROC
    jmp qword ptr [pfn_curl_easy_init]
curl_easy_init ENDP

PUBLIC curl_easy_nextheader
curl_easy_nextheader PROC
    jmp qword ptr [pfn_curl_easy_nextheader]
curl_easy_nextheader ENDP

PUBLIC curl_easy_option_by_id
curl_easy_option_by_id PROC
    jmp qword ptr [pfn_curl_easy_option_by_id]
curl_easy_option_by_id ENDP

PUBLIC curl_easy_option_by_name
curl_easy_option_by_name PROC
    jmp qword ptr [pfn_curl_easy_option_by_name]
curl_easy_option_by_name ENDP

PUBLIC curl_easy_option_next
curl_easy_option_next PROC
    jmp qword ptr [pfn_curl_easy_option_next]
curl_easy_option_next ENDP

PUBLIC curl_easy_pause
curl_easy_pause PROC
    jmp qword ptr [pfn_curl_easy_pause]
curl_easy_pause ENDP

PUBLIC curl_easy_perform
curl_easy_perform PROC
    jmp qword ptr [pfn_curl_easy_perform]
curl_easy_perform ENDP

PUBLIC curl_easy_recv
curl_easy_recv PROC
    jmp qword ptr [pfn_curl_easy_recv]
curl_easy_recv ENDP

PUBLIC curl_easy_reset
curl_easy_reset PROC
    jmp qword ptr [pfn_curl_easy_reset]
curl_easy_reset ENDP

PUBLIC curl_easy_send
curl_easy_send PROC
    jmp qword ptr [pfn_curl_easy_send]
curl_easy_send ENDP

PUBLIC curl_easy_setopt
curl_easy_setopt PROC
    jmp qword ptr [pfn_curl_easy_setopt]
curl_easy_setopt ENDP

PUBLIC curl_easy_ssls_export
curl_easy_ssls_export PROC
    jmp qword ptr [pfn_curl_easy_ssls_export]
curl_easy_ssls_export ENDP

PUBLIC curl_easy_ssls_import
curl_easy_ssls_import PROC
    jmp qword ptr [pfn_curl_easy_ssls_import]
curl_easy_ssls_import ENDP

PUBLIC curl_easy_strerror
curl_easy_strerror PROC
    jmp qword ptr [pfn_curl_easy_strerror]
curl_easy_strerror ENDP

PUBLIC curl_easy_unescape
curl_easy_unescape PROC
    jmp qword ptr [pfn_curl_easy_unescape]
curl_easy_unescape ENDP

PUBLIC curl_easy_upkeep
curl_easy_upkeep PROC
    jmp qword ptr [pfn_curl_easy_upkeep]
curl_easy_upkeep ENDP

PUBLIC curl_escape
curl_escape PROC
    jmp qword ptr [pfn_curl_escape]
curl_escape ENDP

PUBLIC curl_formadd
curl_formadd PROC
    jmp qword ptr [pfn_curl_formadd]
curl_formadd ENDP

PUBLIC curl_formfree
curl_formfree PROC
    jmp qword ptr [pfn_curl_formfree]
curl_formfree ENDP

PUBLIC curl_formget
curl_formget PROC
    jmp qword ptr [pfn_curl_formget]
curl_formget ENDP

PUBLIC curl_free
curl_free PROC
    jmp qword ptr [pfn_curl_free]
curl_free ENDP

PUBLIC curl_getdate
curl_getdate PROC
    jmp qword ptr [pfn_curl_getdate]
curl_getdate ENDP

PUBLIC curl_getenv
curl_getenv PROC
    jmp qword ptr [pfn_curl_getenv]
curl_getenv ENDP

PUBLIC curl_global_init_mem
curl_global_init_mem PROC
    jmp qword ptr [pfn_curl_global_init_mem]
curl_global_init_mem ENDP

PUBLIC curl_global_sslset
curl_global_sslset PROC
    jmp qword ptr [pfn_curl_global_sslset]
curl_global_sslset ENDP

PUBLIC curl_global_trace
curl_global_trace PROC
    jmp qword ptr [pfn_curl_global_trace]
curl_global_trace ENDP

PUBLIC curl_maprintf
curl_maprintf PROC
    jmp qword ptr [pfn_curl_maprintf]
curl_maprintf ENDP

PUBLIC curl_mfprintf
curl_mfprintf PROC
    jmp qword ptr [pfn_curl_mfprintf]
curl_mfprintf ENDP

PUBLIC curl_mime_addpart
curl_mime_addpart PROC
    jmp qword ptr [pfn_curl_mime_addpart]
curl_mime_addpart ENDP

PUBLIC curl_mime_data
curl_mime_data PROC
    jmp qword ptr [pfn_curl_mime_data]
curl_mime_data ENDP

PUBLIC curl_mime_data_cb
curl_mime_data_cb PROC
    jmp qword ptr [pfn_curl_mime_data_cb]
curl_mime_data_cb ENDP

PUBLIC curl_mime_encoder
curl_mime_encoder PROC
    jmp qword ptr [pfn_curl_mime_encoder]
curl_mime_encoder ENDP

PUBLIC curl_mime_filedata
curl_mime_filedata PROC
    jmp qword ptr [pfn_curl_mime_filedata]
curl_mime_filedata ENDP

PUBLIC curl_mime_filename
curl_mime_filename PROC
    jmp qword ptr [pfn_curl_mime_filename]
curl_mime_filename ENDP

PUBLIC curl_mime_free
curl_mime_free PROC
    jmp qword ptr [pfn_curl_mime_free]
curl_mime_free ENDP

PUBLIC curl_mime_headers
curl_mime_headers PROC
    jmp qword ptr [pfn_curl_mime_headers]
curl_mime_headers ENDP

PUBLIC curl_mime_init
curl_mime_init PROC
    jmp qword ptr [pfn_curl_mime_init]
curl_mime_init ENDP

PUBLIC curl_mime_name
curl_mime_name PROC
    jmp qword ptr [pfn_curl_mime_name]
curl_mime_name ENDP

PUBLIC curl_mime_subparts
curl_mime_subparts PROC
    jmp qword ptr [pfn_curl_mime_subparts]
curl_mime_subparts ENDP

PUBLIC curl_mime_type
curl_mime_type PROC
    jmp qword ptr [pfn_curl_mime_type]
curl_mime_type ENDP

PUBLIC curl_mprintf
curl_mprintf PROC
    jmp qword ptr [pfn_curl_mprintf]
curl_mprintf ENDP

PUBLIC curl_msnprintf
curl_msnprintf PROC
    jmp qword ptr [pfn_curl_msnprintf]
curl_msnprintf ENDP

PUBLIC curl_msprintf
curl_msprintf PROC
    jmp qword ptr [pfn_curl_msprintf]
curl_msprintf ENDP

PUBLIC curl_multi_add_handle
curl_multi_add_handle PROC
    jmp qword ptr [pfn_curl_multi_add_handle]
curl_multi_add_handle ENDP

PUBLIC curl_multi_assign
curl_multi_assign PROC
    jmp qword ptr [pfn_curl_multi_assign]
curl_multi_assign ENDP

PUBLIC curl_multi_cleanup
curl_multi_cleanup PROC
    jmp qword ptr [pfn_curl_multi_cleanup]
curl_multi_cleanup ENDP

PUBLIC curl_multi_fdset
curl_multi_fdset PROC
    jmp qword ptr [pfn_curl_multi_fdset]
curl_multi_fdset ENDP

PUBLIC curl_multi_get_handles
curl_multi_get_handles PROC
    jmp qword ptr [pfn_curl_multi_get_handles]
curl_multi_get_handles ENDP

PUBLIC curl_multi_get_offt
curl_multi_get_offt PROC
    jmp qword ptr [pfn_curl_multi_get_offt]
curl_multi_get_offt ENDP

PUBLIC curl_multi_info_read
curl_multi_info_read PROC
    jmp qword ptr [pfn_curl_multi_info_read]
curl_multi_info_read ENDP

PUBLIC curl_multi_init
curl_multi_init PROC
    jmp qword ptr [pfn_curl_multi_init]
curl_multi_init ENDP

PUBLIC curl_multi_notify_disable
curl_multi_notify_disable PROC
    jmp qword ptr [pfn_curl_multi_notify_disable]
curl_multi_notify_disable ENDP

PUBLIC curl_multi_notify_enable
curl_multi_notify_enable PROC
    jmp qword ptr [pfn_curl_multi_notify_enable]
curl_multi_notify_enable ENDP

PUBLIC curl_multi_perform
curl_multi_perform PROC
    jmp qword ptr [pfn_curl_multi_perform]
curl_multi_perform ENDP

PUBLIC curl_multi_poll
curl_multi_poll PROC
    jmp qword ptr [pfn_curl_multi_poll]
curl_multi_poll ENDP

PUBLIC curl_multi_remove_handle
curl_multi_remove_handle PROC
    jmp qword ptr [pfn_curl_multi_remove_handle]
curl_multi_remove_handle ENDP

PUBLIC curl_multi_setopt
curl_multi_setopt PROC
    jmp qword ptr [pfn_curl_multi_setopt]
curl_multi_setopt ENDP

PUBLIC curl_multi_socket
curl_multi_socket PROC
    jmp qword ptr [pfn_curl_multi_socket]
curl_multi_socket ENDP

PUBLIC curl_multi_socket_action
curl_multi_socket_action PROC
    jmp qword ptr [pfn_curl_multi_socket_action]
curl_multi_socket_action ENDP

PUBLIC curl_multi_socket_all
curl_multi_socket_all PROC
    jmp qword ptr [pfn_curl_multi_socket_all]
curl_multi_socket_all ENDP

PUBLIC curl_multi_strerror
curl_multi_strerror PROC
    jmp qword ptr [pfn_curl_multi_strerror]
curl_multi_strerror ENDP

PUBLIC curl_multi_timeout
curl_multi_timeout PROC
    jmp qword ptr [pfn_curl_multi_timeout]
curl_multi_timeout ENDP

PUBLIC curl_multi_wait
curl_multi_wait PROC
    jmp qword ptr [pfn_curl_multi_wait]
curl_multi_wait ENDP

PUBLIC curl_multi_waitfds
curl_multi_waitfds PROC
    jmp qword ptr [pfn_curl_multi_waitfds]
curl_multi_waitfds ENDP

PUBLIC curl_multi_wakeup
curl_multi_wakeup PROC
    jmp qword ptr [pfn_curl_multi_wakeup]
curl_multi_wakeup ENDP

PUBLIC curl_mvaprintf
curl_mvaprintf PROC
    jmp qword ptr [pfn_curl_mvaprintf]
curl_mvaprintf ENDP

PUBLIC curl_mvfprintf
curl_mvfprintf PROC
    jmp qword ptr [pfn_curl_mvfprintf]
curl_mvfprintf ENDP

PUBLIC curl_mvprintf
curl_mvprintf PROC
    jmp qword ptr [pfn_curl_mvprintf]
curl_mvprintf ENDP

PUBLIC curl_mvsnprintf
curl_mvsnprintf PROC
    jmp qword ptr [pfn_curl_mvsnprintf]
curl_mvsnprintf ENDP

PUBLIC curl_mvsprintf
curl_mvsprintf PROC
    jmp qword ptr [pfn_curl_mvsprintf]
curl_mvsprintf ENDP

PUBLIC curl_pushheader_byname
curl_pushheader_byname PROC
    jmp qword ptr [pfn_curl_pushheader_byname]
curl_pushheader_byname ENDP

PUBLIC curl_pushheader_bynum
curl_pushheader_bynum PROC
    jmp qword ptr [pfn_curl_pushheader_bynum]
curl_pushheader_bynum ENDP

PUBLIC curl_share_cleanup
curl_share_cleanup PROC
    jmp qword ptr [pfn_curl_share_cleanup]
curl_share_cleanup ENDP

PUBLIC curl_share_init
curl_share_init PROC
    jmp qword ptr [pfn_curl_share_init]
curl_share_init ENDP

PUBLIC curl_share_setopt
curl_share_setopt PROC
    jmp qword ptr [pfn_curl_share_setopt]
curl_share_setopt ENDP

PUBLIC curl_share_strerror
curl_share_strerror PROC
    jmp qword ptr [pfn_curl_share_strerror]
curl_share_strerror ENDP

PUBLIC curl_slist_append
curl_slist_append PROC
    jmp qword ptr [pfn_curl_slist_append]
curl_slist_append ENDP

PUBLIC curl_slist_free_all
curl_slist_free_all PROC
    jmp qword ptr [pfn_curl_slist_free_all]
curl_slist_free_all ENDP

PUBLIC curl_strequal
curl_strequal PROC
    jmp qword ptr [pfn_curl_strequal]
curl_strequal ENDP

PUBLIC curl_strnequal
curl_strnequal PROC
    jmp qword ptr [pfn_curl_strnequal]
curl_strnequal ENDP

PUBLIC curl_unescape
curl_unescape PROC
    jmp qword ptr [pfn_curl_unescape]
curl_unescape ENDP

PUBLIC curl_url
curl_url PROC
    jmp qword ptr [pfn_curl_url]
curl_url ENDP

PUBLIC curl_url_cleanup
curl_url_cleanup PROC
    jmp qword ptr [pfn_curl_url_cleanup]
curl_url_cleanup ENDP

PUBLIC curl_url_dup
curl_url_dup PROC
    jmp qword ptr [pfn_curl_url_dup]
curl_url_dup ENDP

PUBLIC curl_url_get
curl_url_get PROC
    jmp qword ptr [pfn_curl_url_get]
curl_url_get ENDP

PUBLIC curl_url_set
curl_url_set PROC
    jmp qword ptr [pfn_curl_url_set]
curl_url_set ENDP

PUBLIC curl_url_strerror
curl_url_strerror PROC
    jmp qword ptr [pfn_curl_url_strerror]
curl_url_strerror ENDP

PUBLIC curl_version
curl_version PROC
    jmp qword ptr [pfn_curl_version]
curl_version ENDP

PUBLIC curl_version_info
curl_version_info PROC
    jmp qword ptr [pfn_curl_version_info]
curl_version_info ENDP

PUBLIC curl_ws_meta
curl_ws_meta PROC
    jmp qword ptr [pfn_curl_ws_meta]
curl_ws_meta ENDP

PUBLIC curl_ws_recv
curl_ws_recv PROC
    jmp qword ptr [pfn_curl_ws_recv]
curl_ws_recv ENDP

PUBLIC curl_ws_send
curl_ws_send PROC
    jmp qword ptr [pfn_curl_ws_send]
curl_ws_send ENDP

PUBLIC curl_ws_start_frame
curl_ws_start_frame PROC
    jmp qword ptr [pfn_curl_ws_start_frame]
curl_ws_start_frame ENDP

END
