//! Steam news / patch notes via the public ISteamNews/GetNewsForApp endpoint,
//! cached per app. No Web API key required. Fetched from Rust because the
//! frontend CSP forbids external requests.

use std::collections::HashMap;
use std::path::PathBuf;

use serde::{Deserialize, Serialize};
use tauri::AppHandle;

use crate::output_dir::resolve_downloads_dir;

const NEWS_URL: &str = "https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/";
/// News changes more often than ownership, but not minute-to-minute.
const CACHE_FRESH_SECS: i64 = 60 * 60;

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct NewsItem {
    #[serde(default)]
    pub gid: String,
    #[serde(default)]
    pub title: String,
    #[serde(default)]
    pub url: String,
    #[serde(default)]
    pub author: String,
    #[serde(default)]
    pub contents: String,
    #[serde(default)]
    pub feedlabel: String,
    #[serde(default)]
    pub date: u64,
    #[serde(default)]
    pub feedname: String,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct NewsResult {
    pub items: Vec<NewsItem>,
    pub fetched_at: String,
    pub from_cache: bool,
}

// ----- Steam API response shapes -----
#[derive(Debug, Deserialize)]
struct NewsResponse {
    appnews: Option<AppNews>,
}

#[derive(Debug, Deserialize)]
struct AppNews {
    #[serde(default)]
    newsitems: Vec<NewsItem>,
}

// ----- Cache -----
#[derive(Clone, Serialize, Deserialize)]
struct CachedNews {
    fetched_at: String,
    items: Vec<NewsItem>,
}

#[derive(Default, Serialize, Deserialize)]
struct NewsCache {
    #[serde(default)]
    apps: HashMap<String, CachedNews>,
}

fn cache_path(app_handle: &AppHandle) -> Result<PathBuf, String> {
    let dir = resolve_downloads_dir(app_handle)?.join(".cache");
    std::fs::create_dir_all(&dir).map_err(|e| format!("Failed to create cache dir: {e}"))?;
    Ok(dir.join("news.json"))
}

fn read_cache(path: &PathBuf) -> NewsCache {
    std::fs::read_to_string(path)
        .ok()
        .and_then(|raw| serde_json::from_str(&raw).ok())
        .unwrap_or_default()
}

fn write_cache(path: &PathBuf, cache: &NewsCache) {
    if let Ok(json) = serde_json::to_string(cache) {
        let _ = std::fs::write(path, json);
    }
}

fn age_secs(fetched_at: &str) -> Option<i64> {
    let then = chrono::DateTime::parse_from_rfc3339(fetched_at).ok()?;
    Some((chrono::Utc::now() - then.with_timezone(&chrono::Utc)).num_seconds())
}

fn fetch_news(appid: u32, count: u32) -> Result<Vec<NewsItem>, String> {
    let url = format!(
        "{NEWS_URL}?appid={appid}&count={count}&maxlength=0&format=json"
    );
    let response = reqwest::blocking::Client::builder()
        .timeout(std::time::Duration::from_secs(20))
        .build()
        .map_err(|e| format!("Failed to build HTTP client: {e}"))?
        .get(&url)
        .send()
        .map_err(|e| format!("Failed to fetch news: {e}"))?;

    if !response.status().is_success() {
        return Err(format!("Steam news API returned {}", response.status()));
    }

    let parsed: NewsResponse = response
        .json()
        .map_err(|e| format!("Failed to parse news response: {e}"))?;
    Ok(parsed.appnews.map(|a| a.newsitems).unwrap_or_default())
}

#[tauri::command]
pub async fn get_app_news(
    app_handle: AppHandle,
    appid: u32,
    count: Option<u32>,
    force: Option<bool>,
) -> Result<NewsResult, String> {
    let count = count.unwrap_or(10).clamp(1, 50);
    let force = force.unwrap_or(false);
    let key = appid.to_string();

    let cache_file = cache_path(&app_handle).ok();
    if !force {
        if let Some(ref path) = cache_file {
            if let Some(entry) = read_cache(path).apps.get(&key) {
                if age_secs(&entry.fetched_at).unwrap_or(i64::MAX) < CACHE_FRESH_SECS {
                    return Ok(NewsResult {
                        items: entry.items.clone(),
                        fetched_at: entry.fetched_at.clone(),
                        from_cache: true,
                    });
                }
            }
        }
    }

    let items = tauri::async_runtime::spawn_blocking(move || fetch_news(appid, count))
        .await
        .map_err(|e| format!("News task failed to run: {e}"))??;

    let fetched_at = chrono::Utc::now().to_rfc3339();
    if let Some(ref path) = cache_file {
        let mut cache = read_cache(path);
        cache.apps.insert(
            key,
            CachedNews {
                fetched_at: fetched_at.clone(),
                items: items.clone(),
            },
        );
        write_cache(path, &cache);
    }

    Ok(NewsResult {
        items,
        fetched_at,
        from_cache: false,
    })
}
