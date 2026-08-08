# PodoBot

개인용 치지직 방송 서포터.

## 방송자가 받는 파일

방송 사용자에게 전달하는 파일은 이것 하나입니다.

```text
PodoBotSetup.exe
```

방송 사용자는 .NET SDK, Inno Setup, 터미널, CMD를 설치하거나 사용할 필요가 없습니다.

## 설치 후 사용

1. `PodoBotSetup.exe` 실행
2. 원하는 설치 폴더 선택
3. 설치 완료 후 PodoBot 실행
4. `치지직 연결하기`
5. 브라우저에서 승인
6. 이후 방송할 때 `방송봇 켜기`

## 기본 기능

- `!명령어`
- 확률 룰렛
- 반복 타이머
- 카운터
- 전체/개인 쿨다운
- 전체/매니저/스트리머 권한
- OBS 룰렛 오버레이
- 로컬 설정 저장
- 치지직 토큰 Windows DPAPI 암호화 저장

## OBS

브라우저 소스 주소:

```text
http://localhost:18766/roulette
```

## 개발자 배포

배포 빌드는 GitHub Actions가 생성합니다.

자세한 설정은:

```text
docs/DEVELOPER_RELEASE.md
```

를 확인하세요.
