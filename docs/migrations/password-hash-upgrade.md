# 密碼雜湊升級：加寬 Password 欄位並啟用 PBKDF2

本文件說明如何把使用者密碼的儲存格式，從無鹽 MD5 升級為加鹽的 PBKDF2-HMAC-SHA256。

**預設行為完全不變。** 套用本版本後，預設演算法仍是 MD5，資料庫與登入行為都不會有任何改變。只有當維護者同時做到下列兩件事，PBKDF2 才會生效：

1. 把資料庫裡的 `Password` 欄位加寬（本文件第 3 節）；
2. 設定 `PasswordOptions:Algorithm = Pbkdf2Sha256`（第 4 節）。

**啟用是不可逆的一步。** 一旦有使用者被升級成 PBKDF2，較舊、不認得這個格式的 WTM 版本就無法讓這些使用者登入（第 6 節）。啟用前請務必先備份、確認回滾方式。

## 1. 為什麼要加寬欄位

| 格式 | 外觀 | 長度 |
|---|---|---|
| 舊格式 MD5 | 32 個十六進位字元 | 固定 32 |
| PBKDF2 | `$pbkdf2-sha256$<迭代次數>$<salt，Base64 24 字元>$<hash，Base64 44 字元>` | 預設迭代次數 600000 時為 **91** 字元；迭代次數最高 2000000（7 位數）時為 92 字元 |

過去的 `FrameworkUserBase.Password` 欄位是 32 字元（SQL Server：`nvarchar(32) NOT NULL`），放不下 PBKDF2。本版本把模型改成 128 字元（`[StringLength(128)]`），建議資料庫的欄位也加寬到 128：

- **新建的資料庫**（由 `EnsureCreated` 建立）：直接就是 128，不需要做任何事。
- **既有的資料庫**：`EnsureCreated` 不會修改已存在的資料表，欄位仍然是 32。你必須自己執行第 3 節的 SQL。

### 安全防線（為什麼不會因為忘了加寬而寫壞資料）

模型說 128，並不代表資料庫真的是 128。因此當設定了 `Pbkdf2Sha256` 時，程式會**實際查詢資料庫的欄位長度**（SQL Server／MySQL／PostgreSQL 查 `INFORMATION_SCHEMA.COLUMNS`；Oracle／達夢查 `ALL_TAB_COLUMNS`；SQLite 與 EF InMemory 不限制長度，不需查詢），結果有三種：

- 欄位夠長（或不限長度）→ PBKDF2 生效；
- 欄位太短，或查不到（連不上、找不到資料表或欄位、權限不足、不支援的資料庫）→ **自動退回 MD5，並記錄一則 Warning**，Warning 中會指向本文件；
- 預設 MD5 時完全不會做這個查詢，沒有任何額外負擔。

查詢結果的快取規則：確定的答案快取約 1 小時，「查不到」的結果快取約 5 分鐘，之後都會重新查詢；同一個欄位同一時間只會有一個請求去查資料庫，其他同時進來的請求不會等待，會先拿到上一次的答案（還沒有答案時視同「查不到」，也就是暫時用 MD5）。資料庫連不上時，最多只會讓一個請求短暫等待（連線逾時約 5 秒）。為了立即生效，**加寬欄位之後、以及任何一次結構回滾之後，都請重新啟動應用程式**。

### 部署新版本但尚未加寬欄位時

這是最常見的情況，**不會影響啟動與登入**：

- 預設演算法是 MD5 時，欄位檢查器**完全不會被呼叫**；MD5 雜湊是 32 字元，放得進既有的 32 字元欄位。
- `EnsureCreated` 不會修改已存在的資料表，所以模型雖然已經寫著 128，資料庫裡的欄位仍然是 32；EF 不會在執行期檢查長度，登入、建立使用者、修改密碼都照舊運作。
- 只有**啟用 PBKDF2** 才依賴欄位加寬；在加寬之前啟用，系統會退回 MD5 並記錄 Warning，**不會**讓啟動或登入失敗。
- 沒有使用 EF Core Migrations、自己手動管理資料庫結構的應用程式，部署時**不需要任何 migration**；要啟用 PBKDF2 時再依第 3 節手動加寬欄位即可。使用 EF Migrations 的應用程式請見第 3 節最後一段。

## 2. 事前檢查

先確認欄位現況（資料表名稱預設是 `FrameworkUsers`，欄位是 `Password`；如果你的應用程式把使用者類別對應到其他資料表、其他 schema，請換成你實際的名稱，可看你的 `DbContext` 或 `[Table(...)]`）。

SQL Server：

```sql
SELECT TABLE_SCHEMA, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, IS_NULLABLE, COLLATION_NAME,
       DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS DATABASE_COLLATION
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = SCHEMA_NAME() AND TABLE_NAME = 'FrameworkUsers' AND COLUMN_NAME = 'Password';
-- CHARACTER_MAXIMUM_LENGTH：32 表示尚未加寬；-1 表示 MAX（可直接使用）
-- COLLATION_NAME 與 DATABASE_COLLATION 相同時，§3 的 ALTER 不必寫 COLLATE；不同時要把 COLLATION_NAME 照抄進 ALTER
```

記下這一列的 `IS_NULLABLE` 與 `COLLATION_NAME`，§3 加寬後再跑一次，確認兩者都沒變。資料庫定序與伺服器預設定序不同（例如 `Chinese_Taiwan_Stroke_CI_AS` 的資料庫放在 `SQL_Latin1_General_CP1_CI_AS` 的伺服器上）很常見，比較的對象是**資料庫**定序，不是伺服器的。

沒有明確指定 schema 時，程式查的是連線登入帳號的預設 schema（`SCHEMA_NAME()`）。如果你的資料表在其他 schema，而 Warning 說「找不到 Password 欄位」，請在 `DbContext` 對應中明確指定該 schema（`ToTable("FrameworkUsers", "你的schema")`），或把登入帳號的預設 schema 設成資料表所在的 schema。

MySQL：

```sql
SELECT CHARACTER_MAXIMUM_LENGTH, IS_NULLABLE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'FrameworkUsers' AND COLUMN_NAME = 'Password';
```

PostgreSQL（名稱區分大小寫）：

```sql
SELECT character_maximum_length, is_nullable
FROM information_schema.columns
WHERE table_schema = current_schema() AND table_name = 'FrameworkUsers' AND column_name = 'Password';
```

Oracle／達夢：

```sql
SELECT CHAR_LENGTH, DATA_LENGTH, DATA_TYPE, NULLABLE
FROM ALL_TAB_COLUMNS
WHERE OWNER = SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA') AND TABLE_NAME = 'FrameworkUsers' AND COLUMN_NAME = 'Password';
```

## 3. 加寬欄位

由 `EnsureCreated` 建立的 `Password` 欄位是 **NOT NULL**。SQL Server 的 `ALTER COLUMN` 會重設可為 Null 的設定，所以一定要明確寫出 `NOT NULL`，否則欄位會變成可為 Null。

**SQL Server**

```sql
ALTER TABLE [FrameworkUsers] ALTER COLUMN [Password] NVARCHAR(128) NOT NULL;
```

（有自訂 schema 時寫成 `[dbo].[FrameworkUsers]`。把 `nvarchar(32)` 加寬為 `nvarchar(128)` 通常只改中繼資料，很快；仍建議在低峰時段執行。）

注意：SQL Server 的 `ALTER COLUMN` 會把欄位的定序（COLLATE）重設為資料庫預設值。如果原欄位使用非預設的定序，請在型別後面重複指定，例如 `NVARCHAR(128) COLLATE Chinese_Taiwan_Stroke_CI_AS NOT NULL`；原定序可從 `INFORMATION_SCHEMA.COLUMNS.COLLATION_NAME` 查到；第 2 節的查詢會同時列出 `COLLATION_NAME` 與資料庫預設定序（`DATABASE_COLLATION`），兩者相同就不必寫 `COLLATE`。

**MySQL**

```sql
ALTER TABLE `FrameworkUsers` MODIFY COLUMN `Password` VARCHAR(128) NOT NULL;
-- 若原欄位有指定字元集，先用 SHOW CREATE TABLE FrameworkUsers 查看，並在 VARCHAR(128) 後面補上相同的 CHARACTER SET / COLLATE
```

**PostgreSQL**

```sql
ALTER TABLE "FrameworkUsers" ALTER COLUMN "Password" TYPE character varying(128);
```

**Oracle**

先確認現有型別（第 2 節的查詢，看 `DATA_TYPE`）：EF 預設產生的是 `NVARCHAR2(32)`，但你的環境也可能是 `VARCHAR2(32)`。加寬時請**保持同一種型別**，只改長度：

```sql
-- 現有型別是 NVARCHAR2
ALTER TABLE "FrameworkUsers" MODIFY ("Password" NVARCHAR2(128));
-- 現有型別是 VARCHAR2（依原本的長度語意加上 CHAR 或 BYTE）
ALTER TABLE "FrameworkUsers" MODIFY ("Password" VARCHAR2(128 CHAR));
-- MODIFY 不會動到既有的 NOT NULL
```

**達夢（DaMeng）**

```sql
-- 先用第 2 節的查詢確認現有型別，保持同一類型（VARCHAR 或 NVARCHAR），只改長度
ALTER TABLE "FrameworkUsers" MODIFY "Password" VARCHAR(128);
```

達夢的語法與欄位長度的判斷，本文件與程式都是依官方文件撰寫，**尚未在真實的達夢資料庫上驗證**。**目前在 EF Core 10 上，達夢的 EF provider 套件無法建立模型，所以達夢環境暫時無法啟用 PBKDF2**：欄位檢查會得到「無法確認」，系統一律退回 MD5 並記錄 Warning（安全的一面），登入不受影響。此限制消除之前，請勿在達夢環境依賴本功能。

**SQLite**

不需要做任何事。SQLite 不限制欄位長度（`EnsureCreated` 產生的是 `TEXT`）。

**若你的應用程式用 EF Core Migrations 管理結構**：升級套件後，模型的 `Password` 變成 128，請照一般流程加一個 migration（內容會是 `AlterColumn`，32 → 128）並套用，效果與上面的 SQL 相同。在 EF Core 9 之後，如果模型與 migration 快照不一致，`Database.Migrate()` 預設會丟出 `PendingModelChangesWarning`，所以請在升級的同一次部署加入這個 migration。

## 4. 啟用 PBKDF2

依序進行，每一步都完成再做下一步：

1. **確認所有執行個體都能認得兩種格式。** 同一個資料庫若有多個應用程式或多個執行個體，全部都要先升級到本版本（或更新版本）。舊版本遇到 PBKDF2 的雜湊會判定密碼錯誤。
2. **檢查應用程式自己的密碼程式碼。** 搜尋直接呼叫 `Utils.GetMD5String` 來**比對**或**寫入**密碼的地方（常見於自訂的登入、修改密碼、重設密碼、新增使用者、匯入使用者等 ViewModel 或 Controller）。這些程式碼不經過 `PasswordHasher`：啟用 PBKDF2 後，帳號一旦經框架登入升級為 PBKDF2 格式，自訂的比對就會失敗；自訂的寫入則會繼續寫入 MD5（仍可登入，但不會受到 PBKDF2 保護）。請先改用 `WTMContext.VerifyPassword` 比對、`WTMContext.HashPassword` 產生雜湊，再啟用。
3. **備份 Password 欄位。** 備份表內含密碼雜湊，屬於敏感資料，請限制存取權限並訂好保留期限。

   SQL Server：

   ```sql
   SELECT [ID], [ITCode], [Password] INTO [FrameworkUsers_PasswordBackup_20261019] FROM [FrameworkUsers];
   ```

   MySQL、SQLite：

   ```sql
   CREATE TABLE FrameworkUsers_PasswordBackup_20261019 AS SELECT ID, ITCode, Password FROM FrameworkUsers;
   ```

   PostgreSQL（名稱區分大小寫，要加雙引號）：

   ```sql
   CREATE TABLE "FrameworkUsers_PasswordBackup_20261019" AS SELECT "ID", "ITCode", "Password" FROM "FrameworkUsers";
   ```

   Oracle（識別字區分大小寫，要加雙引號）：

   ```sql
   CREATE TABLE "FrameworkUsers_PasswordBackup_20261019" AS SELECT "ID", "ITCode", "Password" FROM "FrameworkUsers";
   ```

   達夢（識別字區分大小寫，要加雙引號；語法依官方文件，未實測）：

   ```sql
   CREATE TABLE "FrameworkUsers_PasswordBackup_20261019" AS SELECT "ID", "ITCode", "Password" FROM "FrameworkUsers";
   ```

   日期改成備份當天。備份裡包含 `ITCode`，是為了回滾時能認出是哪些帳號。如果你的資料表在特定 schema，請加上 schema 名稱。
4. **加寬欄位**（第 3 節的 SQL）。
5. **部署本版本。**
6. **設定組態**（`appsettings.json` 或環境變數 `PasswordOptions__Algorithm`）：

   ```json
   {
     "PasswordOptions": {
       "Algorithm": "Pbkdf2Sha256",
       "Pbkdf2Iterations": 600000
     }
   }
   ```

   `Pbkdf2Iterations` 可省略（預設 600000，允許範圍 100000～2000000，超出範圍會被限制並記錄 Warning）。迭代次數越高越安全，但每次登入的 CPU 成本也越高，正式上線前請先壓測。
7. **重新啟動應用程式，並執行一次登入**（或建立／修改一次密碼）。欄位長度的檢查是在第一次用到密碼雜湊時才進行，不是在啟動的瞬間。
8. **確認紀錄中沒有退回的 Warning。** 如果欄位沒有加寬、查不到，或其他原因，會看到這樣的 Warning（內含原因與本文件的路徑）：

   ```
   PasswordOptions:Algorithm is Pbkdf2Sha256, but the password column allows only 32 characters and the PBKDF2 format needs 91. New passwords keep using MD5 until the column is widened; see docs/migrations/password-hash-upgrade.md.
   PasswordOptions:Algorithm is Pbkdf2Sha256, but the length of the password column could not be verified (...), so new passwords keep using MD5. ...
   ```

   沒有這些 Warning，表示 PBKDF2 已經生效。也可以用 SQL 確認：登入過的使用者，`Password` 會以 `$pbkdf2-sha256$` 開頭。

   ```sql
   SELECT COUNT(*) FROM FrameworkUsers WHERE Password LIKE '$pbkdf2-sha256$%';
   ```

### 使用者如何被升級

- 使用者**下一次以本機密碼成功登入**時，系統會在驗證通過後，把該使用者的 MD5 雜湊換成 PBKDF2（只更新 `Password` 這一個欄位，而且只在資料列仍是剛剛驗證的那個雜湊時才寫入，不會蓋掉同時發生的改密碼）。
- 新建使用者、修改密碼、匯入使用者，一律直接以 PBKDF2 儲存。
- 外部驗證（SSO、AD 等）的登入，以及用 token 重新載入使用者的流程，**從不寫入 Password 欄位**。這類使用者不會被升級，也不需要。
- 從未再次登入的使用者會一直保留 MD5。這是預期行為；如果要統一，請公告期限後對仍是 MD5 的帳號強制重設密碼。

## 5. 回滾

先判斷你目前處於哪一個階段：

| 階段 | 可以怎麼做 | 注意 |
|---|---|---|
| (a) **只加寬了欄位，尚未啟用 PBKDF2**（組態仍是 MD5 或沒設定） | 可以縮回 32：`ALTER TABLE ... ALTER COLUMN [Password] NVARCHAR(32) NOT NULL`（其他資料庫類推）。 | 所有值都是 32 字元 MD5，不會截斷。縮回前先確認 `SELECT COUNT(*) FROM FrameworkUsers WHERE LEN(Password) > 32` 為 0（MySQL／PostgreSQL／Oracle 用 `LENGTH`）。縮回後請重新啟動應用程式。 |
| (b) **已啟用，想停止升級更多使用者** | 把 `PasswordOptions:Algorithm` 改回 `Md5`（或移除該設定），重新啟動。 | 已經是 PBKDF2 的使用者**仍然可以登入**（兩種格式都能驗證），只是不再有新的升級；之後建立或修改密碼的使用者會存成 MD5。已存在的 PBKDF2 雜湊不會被降回 MD5。**這不需要動資料庫，是最安全的回頭路。** |
| (c) **要回到不認得 PBKDF2 的舊版 WTM** | 先停止應用程式，再還原備份（見下方），其餘的人需要重設密碼。 | 見下方說明。**絕對不要在還有 PBKDF2 值的情況下縮小欄位**，會截斷或讓寫入失敗，使用者將無法登入。 |

### (c) 的做法

PBKDF2 是單向的，無法轉回 MD5，所以唯一的還原來源就是升級前做的備份（因此備份要保留到你確定不會回滾為止）。有些帳號無法從備份還原：備份之後才建立的帳號（備份裡沒有他們），以及備份之後自行改過密碼的帳號（還原會把他們退回舊密碼）。這些帳號要改成「暫時無法登入，必須由管理員重設密碼」，這樣最後才不會留下任何 PBKDF2 的值。

下面的 `占位值` 是任何一個不可能是 MD5 的 32 字元字串（含有 `!`，不是 32 個十六進位字元）；沒有任何密碼能與它相符，所以持有它的帳號無法登入。SQL Server 可以每列隨機產生：`LEFT('!' + REPLACE(CAST(NEWID() AS varchar(36)), '-', ''), 32)`；其他資料庫用固定值 `'!reset-required-0000000000000000'`（剛好 32 字元）即可，因為它同樣不可能相符。

1. 停止所有應用程式執行個體（避免還原到一半又有人登入、被重新升級）。
2. 先處理「已知在備份之後改過密碼」的帳號（用你既有的稽核紀錄，例如操作紀錄，找出這些帳號）：設為占位值，**不要還原**，因為還原會讓他們回到可能已忘記的舊密碼。

   ```sql
   UPDATE FrameworkUsers SET Password = '!reset-required-0000000000000000' WHERE ITCode IN ('帳號1', '帳號2');
   ```
3. 其餘仍是 PBKDF2 格式、且備份裡有資料的資料列，用備份還原 MD5（這是標準 SQL；識別字請依你的資料庫加引號，備份表名稱依你建立的為準）：

   ```sql
   UPDATE FrameworkUsers
   SET Password = (SELECT b.Password FROM FrameworkUsers_PasswordBackup_20261019 b WHERE b.ID = FrameworkUsers.ID)
   WHERE Password LIKE '$pbkdf2-sha256$%'
     AND EXISTS (SELECT 1 FROM FrameworkUsers_PasswordBackup_20261019 b WHERE b.ID = FrameworkUsers.ID);
   ```

   這會把這些使用者還原成備份當時的密碼；如果你無法確定哪些帳號在備份之後改過密碼，保守的做法是跳過這一步，對所有帳號走第 4 步並全部重設。
4. 仍然是 PBKDF2 的資料列，就是備份裡沒有的帳號（備份之後才建立），無法還原，一律設為占位值：

   ```sql
   UPDATE FrameworkUsers SET Password = '!reset-required-0000000000000000' WHERE Password LIKE '$pbkdf2-sha256$%';
   ```
5. 確認已經沒有 PBKDF2 的值：`SELECT COUNT(*) FROM FrameworkUsers WHERE Password LIKE '$pbkdf2-sha256$%'` 必須是 0。
6. 部署舊版本，並**重新啟動應用程式**。
7. 由管理員為所有占位值的帳號重設密碼（找出這些帳號：`SELECT ITCode FROM FrameworkUsers WHERE Password LIKE '!%'`），並通知這些使用者。
8. 欄位可以保持 128，不需要縮回。確定不再需要之後才考慮縮回，而且必須先通過 (a) 的檢查。
9. 備份表確認不再需要時請刪除。

## 6. 注意事項

- 沒有雙格式驗證器的 WTM 版本（也就是不含本功能的版本）**不能讓 PBKDF2 的使用者登入**。部署順序請務必是：所有執行個體先升級 → 再啟用。
- 若使用者類別有自訂（例如隱藏了 `Password` 屬性並自己指定長度），保護機制會同時參考模型與資料庫，取較小者；兩者都夠長才會啟用 PBKDF2。
- 啟用後，每次登入的密碼驗證需要做 PBKDF2 運算（迭代次數越高越耗 CPU）。對不存在或仍是 MD5 的帳號，系統也會做等量的運算，以免從回應時間看出帳號是否存在。
- 設定 `Algorithm` 之後如果資料庫暫時連不上，系統會安全地退回 MD5 並記錄 Warning，連線恢復、快取過期（約 5 分鐘）後會自動再檢查一次。
