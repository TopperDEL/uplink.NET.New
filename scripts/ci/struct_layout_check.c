/*
 * Emits a CSV mapping the native struct layout in uplink_definitions.h:
 *
 *   <struct>,SIZE,<sizeof>
 *   <struct>,<field>,<offset>
 *
 * Matched by tests/uplink.NET.IntegrationTests/NativeStructLayoutTests.cs
 * against Marshal.SizeOf<T>() / Marshal.OffsetOf<T>(field).
 *
 * Compile against the uplink-c source tree:
 *   cc -I <uplink-c checkout> struct_layout_check.c -o /tmp/struct_layout_check
 *   /tmp/struct_layout_check > struct-layout-actual.csv
 */

#include <stddef.h>
#include <stdio.h>

#include "uplink_definitions.h"

#define PRINT_SIZE(S)       printf("%s,SIZE,%zu\n", #S, sizeof(S))
#define PRINT_OFFSET(S, F)  printf("%s,%s,%zu\n", #S, #F, offsetof(S, F))

int main(void) {
    /* ── Error ─────────────────────────────────────────────────────────── */
    PRINT_SIZE(UplinkError);
    PRINT_OFFSET(UplinkError, code);
    PRINT_OFFSET(UplinkError, message);

    /* ── Config ────────────────────────────────────────────────────────── */
    PRINT_SIZE(UplinkConfig);
    PRINT_OFFSET(UplinkConfig, user_agent);
    PRINT_OFFSET(UplinkConfig, dial_timeout_milliseconds);
    PRINT_OFFSET(UplinkConfig, temp_directory);

    /* ── Permission ────────────────────────────────────────────────────── */
    PRINT_SIZE(UplinkPermission);
    PRINT_OFFSET(UplinkPermission, allow_download);
    PRINT_OFFSET(UplinkPermission, allow_upload);
    PRINT_OFFSET(UplinkPermission, allow_list);
    PRINT_OFFSET(UplinkPermission, allow_delete);
    PRINT_OFFSET(UplinkPermission, not_before);
    PRINT_OFFSET(UplinkPermission, not_after);

    /* ── SharePrefix ───────────────────────────────────────────────────── */
    PRINT_SIZE(UplinkSharePrefix);
    PRINT_OFFSET(UplinkSharePrefix, bucket);
    PRINT_OFFSET(UplinkSharePrefix, prefix);

    /* ── Bucket ────────────────────────────────────────────────────────── */
    PRINT_SIZE(UplinkBucket);
    PRINT_OFFSET(UplinkBucket, name);
    PRINT_OFFSET(UplinkBucket, created);

    /* ── SystemMetadata ────────────────────────────────────────────────── */
    PRINT_SIZE(UplinkSystemMetadata);
    PRINT_OFFSET(UplinkSystemMetadata, created);
    PRINT_OFFSET(UplinkSystemMetadata, expires);
    PRINT_OFFSET(UplinkSystemMetadata, content_length);

    /* ── CustomMetadata ────────────────────────────────────────────────── */
    PRINT_SIZE(UplinkCustomMetadataEntry);
    PRINT_OFFSET(UplinkCustomMetadataEntry, key);
    PRINT_OFFSET(UplinkCustomMetadataEntry, key_length);
    PRINT_OFFSET(UplinkCustomMetadataEntry, value);
    PRINT_OFFSET(UplinkCustomMetadataEntry, value_length);

    PRINT_SIZE(UplinkCustomMetadata);
    PRINT_OFFSET(UplinkCustomMetadata, entries);
    PRINT_OFFSET(UplinkCustomMetadata, count);

    /* ── Object / UploadInfo ───────────────────────────────────────────── */
    PRINT_SIZE(UplinkObject);
    PRINT_OFFSET(UplinkObject, key);
    PRINT_OFFSET(UplinkObject, is_prefix);
    PRINT_OFFSET(UplinkObject, system);
    PRINT_OFFSET(UplinkObject, custom);

    PRINT_SIZE(UplinkUploadInfo);
    PRINT_OFFSET(UplinkUploadInfo, upload_id);
    PRINT_OFFSET(UplinkUploadInfo, key);
    PRINT_OFFSET(UplinkUploadInfo, is_prefix);
    PRINT_OFFSET(UplinkUploadInfo, system);
    PRINT_OFFSET(UplinkUploadInfo, custom);

    /* ── Part ──────────────────────────────────────────────────────────── */
    PRINT_SIZE(UplinkPart);
    PRINT_OFFSET(UplinkPart, part_number);
    PRINT_OFFSET(UplinkPart, size);
    PRINT_OFFSET(UplinkPart, modified);
    PRINT_OFFSET(UplinkPart, etag);
    PRINT_OFFSET(UplinkPart, etag_length);

    /* ── Options ───────────────────────────────────────────────────────── */
    PRINT_SIZE(UplinkUploadOptions);
    PRINT_OFFSET(UplinkUploadOptions, expires);

    PRINT_SIZE(UplinkDownloadOptions);
    PRINT_OFFSET(UplinkDownloadOptions, offset);
    PRINT_OFFSET(UplinkDownloadOptions, length);

    PRINT_SIZE(UplinkListBucketsOptions);
    PRINT_OFFSET(UplinkListBucketsOptions, cursor);

    PRINT_SIZE(UplinkListObjectsOptions);
    PRINT_OFFSET(UplinkListObjectsOptions, prefix);
    PRINT_OFFSET(UplinkListObjectsOptions, cursor);
    PRINT_OFFSET(UplinkListObjectsOptions, recursive);
    PRINT_OFFSET(UplinkListObjectsOptions, system);
    PRINT_OFFSET(UplinkListObjectsOptions, custom);

    PRINT_SIZE(UplinkCommitUploadOptions);
    PRINT_OFFSET(UplinkCommitUploadOptions, custom_metadata);

    PRINT_SIZE(UplinkListUploadsOptions);
    PRINT_OFFSET(UplinkListUploadsOptions, prefix);
    PRINT_OFFSET(UplinkListUploadsOptions, cursor);
    PRINT_OFFSET(UplinkListUploadsOptions, recursive);
    PRINT_OFFSET(UplinkListUploadsOptions, system);
    PRINT_OFFSET(UplinkListUploadsOptions, custom);

    PRINT_SIZE(UplinkListUploadPartsOptions);
    PRINT_OFFSET(UplinkListUploadPartsOptions, cursor);

    /* ── Result types ──────────────────────────────────────────────────── */
    PRINT_SIZE(UplinkAccessResult);
    PRINT_OFFSET(UplinkAccessResult, access);
    PRINT_OFFSET(UplinkAccessResult, error);

    PRINT_SIZE(UplinkProjectResult);
    PRINT_OFFSET(UplinkProjectResult, project);
    PRINT_OFFSET(UplinkProjectResult, error);

    PRINT_SIZE(UplinkBucketResult);
    PRINT_OFFSET(UplinkBucketResult, bucket);
    PRINT_OFFSET(UplinkBucketResult, error);

    PRINT_SIZE(UplinkUploadResult);
    PRINT_OFFSET(UplinkUploadResult, upload);
    PRINT_OFFSET(UplinkUploadResult, error);

    PRINT_SIZE(UplinkDownloadResult);
    PRINT_OFFSET(UplinkDownloadResult, download);
    PRINT_OFFSET(UplinkDownloadResult, error);

    PRINT_SIZE(UplinkWriteResult);
    PRINT_OFFSET(UplinkWriteResult, bytes_written);
    PRINT_OFFSET(UplinkWriteResult, error);

    PRINT_SIZE(UplinkReadResult);
    PRINT_OFFSET(UplinkReadResult, bytes_read);
    PRINT_OFFSET(UplinkReadResult, error);

    PRINT_SIZE(UplinkStringResult);
    PRINT_OFFSET(UplinkStringResult, string);
    PRINT_OFFSET(UplinkStringResult, error);

    PRINT_SIZE(UplinkObjectResult);
    PRINT_OFFSET(UplinkObjectResult, object);
    PRINT_OFFSET(UplinkObjectResult, error);

    PRINT_SIZE(UplinkUploadInfoResult);
    PRINT_OFFSET(UplinkUploadInfoResult, info);
    PRINT_OFFSET(UplinkUploadInfoResult, error);

    PRINT_SIZE(UplinkCommitUploadResult);
    PRINT_OFFSET(UplinkCommitUploadResult, object);
    PRINT_OFFSET(UplinkCommitUploadResult, error);

    PRINT_SIZE(UplinkPartUploadResult);
    PRINT_OFFSET(UplinkPartUploadResult, part_upload);
    PRINT_OFFSET(UplinkPartUploadResult, error);

    PRINT_SIZE(UplinkPartResult);
    PRINT_OFFSET(UplinkPartResult, part);
    PRINT_OFFSET(UplinkPartResult, error);

    return 0;
}
