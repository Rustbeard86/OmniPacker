//! Rich app metadata from public Steam endpoints (no Web API key): the
//! storefront `appdetails`, live player count, and review summary. Combined into
//! one cached `AppMeta` for the per-game panel (and, later, template tokens).

use std::collections::HashMap;
use std::path::PathBuf;
use std::time::Duration;

use serde::{Deserialize, Serialize};
use serde_json::Value;
use tauri::AppHandle;

use crate::output_dir::resolve_downloads_dir;

const APPDETAILS_URL: &str = "https://store.steampowered.com/api/appdetails";
const PLAYERS_URL: &str =
    "https://api.steampowered.com/ISteamUserStats/GetNumberOfCurrentPlayers/v1/";
const REVIEWS_URL: &str = "https://store.steampowered.com/appreviews/";
/// Metadata changes slowly; player count is the only volatile bit and a few
/// hours stale is fine for display.
const CACHE_FRESH_SECS: i64 = 6 * 60 * 60;

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AppMeta {
    pub appid: u32,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub app_type: String,
    #[serde(default)]
    pub developers: Vec<String>,
    #[serde(default)]
    pub publishers: Vec<String>,
    #[serde(default)]
    pub release_date: String,
    #[serde(default)]
    pub coming_soon: bool,
    #[serde(default)]
    pub genres: Vec<String>,
    #[serde(default)]
    pub windows: bool,
    #[serde(default)]
    pub mac: bool,
    #[serde(default)]
    pub linux: bool,
    #[serde(default)]
    pub languages: String,
    #[serde(default)]
    pub metacritic: Option<u32>,
    #[serde(default)]
    pub dlc_count: usize,
    #[serde(default)]
    pub is_free: bool,
    #[serde(default)]
    pub player_count: Option<u64>,
    #[serde(default)]
    pub review_desc: Option<String>,
    #[serde(default)]
    pub total_reviews: Option<u64>,
    #[serde(default)]
    pub positive_percent: Option<u32>,
}

#[derive(Clone, Serialize, Deserialize)]
struct CachedMeta {
    fetched_at: String,
    meta: AppMeta,
}

#[derive(Default, Serialize, Deserialize)]
struct MetaCache {
    #[serde(default)]
    apps: HashMap<String, CachedMeta>,
}

fn cache_path(app_handle: &AppHandle) -> Result<PathBuf, String> {
    let dir = resolve_downloads_dir(app_handle)?.join(".cache");
    std::fs::create_dir_all(&dir).map_err(|e| format!("Failed to create cache dir: {e}"))?;
    Ok(dir.join("app-meta.json"))
}

fn read_cache(path: &PathBuf) -> MetaCache {
    std::fs::read_to_string(path)
        .ok()
        .and_then(|raw| serde_json::from_str(&raw).ok())
        .unwrap_or_default()
}

fn write_cache(path: &PathBuf, cache: &MetaCache) {
    if let Ok(json) = serde_json::to_string(cache) {
        let _ = std::fs::write(path, json);
    }
}

fn age_secs(fetched_at: &str) -> Option<i64> {
    let then = chrono::DateTime::parse_from_rfc3339(fetched_at).ok()?;
    Some((chrono::Utc::now() - then.with_timezone(&chrono::Utc)).num_seconds())
}

fn client() -> Result<reqwest::blocking::Client, String> {
    reqwest::blocking::Client::builder()
        .timeout(Duration::from_secs(20))
        .build()
        .map_err(|e| format!("Failed to build HTTP client: {e}"))
}

fn strings_from(value: &Value, key: &str) -> Vec<String> {
    value
        .get(key)
        .and_then(|v| v.as_array())
        .map(|arr| {
            arr.iter()
                .filter_map(|v| v.as_str().map(|s| s.to_string()))
                .collect()
        })
        .unwrap_or_default()
}

fn strip_html(s: &str) -> String {
    // Steam's supported_languages is light HTML ("English<strong>*</strong>, …").
    let mut out = String::with_capacity(s.len());
    let mut in_tag = false;
    for ch in s.chars() {
        match ch {
            '<' => in_tag = true,
            '>' => in_tag = false,
            _ if !in_tag => out.push(ch),
            _ => {}
        }
    }
    out.trim().to_string()
}

/// Fills the storefront-derived fields. Returns false if the app has no store
/// page (success=false), leaving `meta` mostly empty.
fn fill_appdetails(client: &reqwest::blocking::Client, appid: u32, meta: &mut AppMeta) -> bool {
    let url = format!("{APPDETAILS_URL}?appids={appid}");
    let Ok(resp) = client.get(&url).send() else {
        return false;
    };
    let Ok(body) = resp.json::<Value>() else {
        return false;
    };
    let entry = &body[appid.to_string()];
    if !entry["success"].as_bool().unwrap_or(false) {
        return false;
    }
    let data = &entry["data"];

    meta.name = data["name"].as_str().unwrap_or_default().to_string();
    meta.app_type = data["type"].as_str().unwrap_or_default().to_string();
    meta.is_free = data["is_free"].as_bool().unwrap_or(false);
    meta.developers = strings_from(data, "developers");
    meta.publishers = strings_from(data, "publishers");
    meta.release_date = data["release_date"]["date"]
        .as_str()
        .unwrap_or_default()
        .to_string();
    meta.coming_soon = data["release_date"]["coming_soon"]
        .as_bool()
        .unwrap_or(false);
    meta.windows = data["platforms"]["windows"].as_bool().unwrap_or(false);
    meta.mac = data["platforms"]["mac"].as_bool().unwrap_or(false);
    meta.linux = data["platforms"]["linux"].as_bool().unwrap_or(false);
    meta.metacritic = data["metacritic"]["score"].as_u64().map(|n| n as u32);
    meta.languages = strip_html(data["supported_languages"].as_str().unwrap_or_default());
    meta.dlc_count = data["dlc"].as_array().map(|a| a.len()).unwrap_or(0);
    meta.genres = data["genres"]
        .as_array()
        .map(|arr| {
            arr.iter()
                .filter_map(|g| g["description"].as_str().map(|s| s.to_string()))
                .collect()
        })
        .unwrap_or_default();
    true
}

fn fill_player_count(client: &reqwest::blocking::Client, appid: u32, meta: &mut AppMeta) {
    let url = format!("{PLAYERS_URL}?appid={appid}");
    if let Ok(resp) = client.get(&url).send() {
        if let Ok(body) = resp.json::<Value>() {
            if body["response"]["result"].as_u64() == Some(1) {
                meta.player_count = body["response"]["player_count"].as_u64();
            }
        }
    }
}

fn fill_reviews(client: &reqwest::blocking::Client, appid: u32, meta: &mut AppMeta) {
    let url = format!("{REVIEWS_URL}{appid}?json=1&language=all&num_per_page=0&purchase_type=all");
    if let Ok(resp) = client.get(&url).send() {
        if let Ok(body) = resp.json::<Value>() {
            let summary = &body["query_summary"];
            meta.review_desc = summary["review_score_desc"]
                .as_str()
                .map(|s| s.to_string());
            let pos = summary["total_positive"].as_u64();
            let total = summary["total_reviews"].as_u64();
            meta.total_reviews = total;
            if let (Some(pos), Some(total)) = (pos, total) {
                if total > 0 {
                    meta.positive_percent = Some(((pos as f64 / total as f64) * 100.0).round() as u32);
                }
            }
        }
    }
}

fn fetch_meta(appid: u32) -> Result<AppMeta, String> {
    let client = client()?;
    let mut meta = AppMeta {
        appid,
        ..Default::default()
    };
    let ok = fill_appdetails(&client, appid, &mut meta);
    // Player count and reviews work even when there's no store page.
    fill_player_count(&client, appid, &mut meta);
    fill_reviews(&client, appid, &mut meta);
    if !ok && meta.name.is_empty() {
        meta.name = format!("App {appid}");
    }
    Ok(meta)
}

#[tauri::command]
pub async fn get_app_meta(
    app_handle: AppHandle,
    appid: u32,
    force: Option<bool>,
) -> Result<AppMeta, String> {
    let force = force.unwrap_or(false);
    let key = appid.to_string();
    let cache_file = cache_path(&app_handle).ok();

    if !force {
        if let Some(ref path) = cache_file {
            if let Some(entry) = read_cache(path).apps.get(&key) {
                if age_secs(&entry.fetched_at).unwrap_or(i64::MAX) < CACHE_FRESH_SECS {
                    return Ok(entry.meta.clone());
                }
            }
        }
    }

    let meta = tauri::async_runtime::spawn_blocking(move || fetch_meta(appid))
        .await
        .map_err(|e| format!("Metadata task failed to run: {e}"))??;

    if let Some(ref path) = cache_file {
        let mut cache = read_cache(path);
        cache.apps.insert(
            key,
            CachedMeta {
                fetched_at: chrono::Utc::now().to_rfc3339(),
                meta: meta.clone(),
            },
        );
        write_cache(path, &cache);
    }

    Ok(meta)
}
