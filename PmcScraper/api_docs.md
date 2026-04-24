# PMC Store API — Documentation for C# Client

**Base URL:** `http://<host>:<port>`

**Authentication:** All requests must include the header:

```
app-sec: bregulator
```

---

## Endpoints

### 1. Health Check

| | |
|---|---|
| **Method** | `GET` |
| **Route** | `/` |
| **Auth** | Required |
| **Parameters** | None |

**Response:**
```json
{ "status": "ok" }
```

---

### 2. Insert Article List (JSON)

| | |
|---|---|
| **Method** | `POST` |
| **Route** | `/articles/list` |
| **Auth** | Required |
| **Body** | `application/json` — `int[]` |

**Request:**
```json
[123456, 789012, 345678]
```

**Response `201`:**
```json
{ "inserted": 3 }
```

---

### 3. Insert Article List (File Upload)

| | |
|---|---|
| **Method** | `POST` |
| **Route** | `/articles/list/upload` |
| **Auth** | Required |
| **Body** | `multipart/form-data` — field name: `file`, `.txt` file, one PMC ID per line |

**Response `201`:**
```json
{ "filename": "ids.txt", "inserted": 500 }
```

---

### 4. Claim Free Articles for a Worker

| | |
|---|---|
| **Method** | `POST` |
| **Route** | `/articles/free` |
| **Auth** | Required |
| **Body** | `application/json` |

**Request:**
```json
{
  "user": "worker-1",
  "batch_size": 100
}
```

**Response `200` — `ArticleListDTO[]`:**
```json
[
  {
    "pmc_id": 123456,
    "scraped": false,
    "scraped_at": null,
    "success": false,
    "success_at": null,
    "error": null,
    "error_at": null,
    "user": null,
    "full_text": false
  }
]
```

---

### 5. Submit Scrape Results

| | |
|---|---|
| **Method** | `POST` |
| **Route** | `/articles/update` |
| **Auth** | Required |
| **Body** | `application/json` |

**Request:**
```json
{
  "user": "worker-1",
  "articles": [
    {
      "pmc_id": 123456,
      "pm_id": 78901,
      "doi": "10.1000/xyz123",
      "title": "Article Title",
      "category": "Research",
      "journal": "Nature",
      "publisher": "Springer",
      "volume": "10",
      "issue": "2",
      "issn": "1234-5678",
      "f_page": "100",
      "l_page": "110",
      "authors": ["Alice Smith", "Bob Jones"],
      "publish_date": "2023-06-15T00:00:00",
      "abstract_text": "Abstract here...",
      "keywords": ["biology", "genetics"],
      "sections": { "Introduction": "Text...", "Conclusion": "Text..." }
    }
  ],
  "full_text_dict": [123456, 789012],
  "success_dict": { "123456": true, "789012": false },
  "error_dict": { "789012": "Timeout error" }
}
```

**Response `200`:**
```json
{ "success": true, "error": null }
```

---

### 6. Get Article Statistics

| | |
|---|---|
| **Method** | `GET` |
| **Route** | `/articles/statics` |
| **Auth** | Required |
| **Parameters** | None |

**Response `200`:**
```json
{
  "Total_List": 1000000,
  "Total_Scraped": 750000,
  "Total_Success": 700000,
  "Total_Full_Text": 500000,
  "Total_Error": 50000
}
```

---

## C# Model Summary

| C# Class | Fields |
|---|---|
| `ArticleListDto` | `int PmcId`, `bool Scraped`, `DateTime? ScrapedAt`, `bool Success`, `DateTime? SuccessAt`, `string? Error`, `DateTime? ErrorAt`, `string? User`, `bool FullText` |
| `GetFreeArticleRequestDto` | `string User`, `int BatchSize = 100` |
| `ScrapeArticleRequestDto` | `string User`, `List<ArticleDto>? Articles`, `List<int>? FullTextDict`, `Dictionary<int,bool> SuccessDict`, `Dictionary<int,string> ErrorDict` |
| `ScrapeArticleResponseDto` | `bool Success`, `string? Error` |
| `ArticleDto` | `int PmcId`, `int? PmId`, `string? Doi`, `string? Title`, `string? Category`, `string? Journal`, `string? Publisher`, `string? Volume`, `string? Issue`, `string? Issn`, `string? FPage`, `string? LPage`, `List<string>? Authors`, `DateTime? PublishDate`, `string? AbstractText`, `List<string>? Keywords`, `Dictionary<string,string>? Sections` |
| `ArticleStaticsDto` | `int TotalList`, `int TotalScraped`, `int TotalSuccess`, `int TotalFullText`, `int TotalError` |

---

## C# HttpClient Notes

- Add `app-sec: bregulator` to every request via `DefaultRequestHeaders`.
- `POST /articles/list` sends `application/json` body as a raw `int[]`.
- `POST /articles/list/upload` uses `MultipartFormDataContent` with a `StreamContent` field named `file`.
- `success_dict` and `error_dict` keys are integers serialized as JSON object keys (strings) — use a custom `JsonConverter` if needed.
- All `datetime` fields use ISO 8601 format (`yyyy-MM-ddTHH:mm:ss`).
