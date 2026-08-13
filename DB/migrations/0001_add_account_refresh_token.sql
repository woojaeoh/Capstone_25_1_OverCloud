-- Phase 2 (3-Tier 인증 계층): Account 테이블에 자체 세션 refresh token 저장용 컬럼 추가.
-- 주의: CloudStorageInfo.refresh_token은 별개(Google/OneDrive/Dropbox OAuth 토큰). 이 마이그레이션과 무관.
--
-- refresh_token은 평문이 아니라 SHA256 해시로 저장한다 (JwtTokenService.HashRefreshToken 참고) —
-- DB가 유출돼도 저장된 값만으로는 세션을 재사용할 수 없게 하기 위함.

ALTER TABLE Account
    ADD COLUMN refresh_token_hash VARCHAR(64) NULL,
    ADD COLUMN refresh_token_expiry DATETIME NULL;
