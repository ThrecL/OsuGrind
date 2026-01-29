export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    const corsHeaders = {
      "Access-Control-Allow-Origin": "*",
      "Access-Control-Allow-Methods": "GET, POST, OPTIONS",
      "Access-Control-Allow-Headers": "Content-Type"
    };
    if (request.method === "OPTIONS") {
      return new Response(null, { headers: corsHeaders });
    }

    if (url.pathname === "/ping" && request.method === "POST") {
      try {
        const { userId, token, stats, graphs } = await request.json();
        if (!userId) return new Response("Missing userId", { status: 400, headers: corsHeaders });
        const now = Date.now();
        let osuId = null;

        // Verify Token & Get Identity (Trusting osu! API)
        if (token) {
            try {
                const osuReq = await fetch("https://osu.ppy.sh/api/v2/me", {
                    headers: { "Authorization": `Bearer ${token}` }
                });
                if (osuReq.ok) {
                    const user = await osuReq.json();
                    osuId = user.id;

                    // SMART WRITE: Only hit D1 if data changed or 6 hours passed
                    const lastSyncKey = `last_sync:${osuId}`;
                    const lastSync = await env.ONLINE_KV.get(lastSyncKey);
                    const shouldUpdate = !lastSync || (now - parseInt(lastSync)) > 21600000 || stats || graphs;

                    if (shouldUpdate) {
                        const bindings = [
                            user.id, user.username, user.country?.code || "XX", user.avatar_url, user.cover?.url || "", now,
                            user.statistics?.ranked_score || 0, user.statistics?.play_count || 0, user.statistics?.level?.current || 0
                        ];
                        const graphsJson = graphs ? JSON.stringify(graphs) : null;
                        const streak = graphs?.streak || 0;
                        
                        if (stats) {
                            bindings.push(stats.totalPlays || 0, stats.totalTime || 0, stats.avgAcc || 0, stats.avgPP || 0, stats.avgUR || 0, stats.form || "Unknown", stats.mentality || 0, graphsJson, streak, stats.perfMatch || 0);
                            await env.DB.prepare(
                                `INSERT INTO accounts (osu_id, username, country, avatar_url, cover_url, last_seen, osu_ranked_score, osu_play_count, osu_level, total_plays, total_time, avg_acc, avg_pp, avg_ur, form, mentality, graphs_json, streak, perf_match) 
                                 VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?) 
                                 ON CONFLICT(osu_id) DO UPDATE SET 
                                   username = ?, country = ?, avatar_url = ?, cover_url = ?, last_seen = ?,
                                   osu_ranked_score = ?, osu_play_count = ?, osu_level = ?,
                                   total_plays = ?, total_time = ?, avg_acc = ?, avg_pp = ?, avg_ur = ?, form = ?, mentality = ?, graphs_json = ?, streak = ?, perf_match = ?`
                            ).bind(
                                ...bindings,
                                user.username, user.country?.code || "XX", user.avatar_url, user.cover?.url || "", now,
                                user.statistics?.ranked_score || 0, user.statistics?.play_count || 0, user.statistics?.level?.current || 0,
                                stats.totalPlays || 0, stats.totalTime || 0, stats.avgAcc || 0, stats.avgPP || 0, stats.avgUR || 0, stats.form || "Unknown", stats.mentality || 0, graphsJson, streak, stats.perfMatch || 0
                            ).run();
                        } else {
                            await env.DB.prepare(
                                `INSERT INTO accounts (osu_id, username, country, avatar_url, cover_url, last_seen, osu_ranked_score, osu_play_count, osu_level) 
                                 VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?) 
                                 ON CONFLICT(osu_id) DO UPDATE SET 
                                   username = ?, country = ?, avatar_url = ?, cover_url = ?, last_seen = ?,
                                   osu_ranked_score = ?, osu_play_count = ?, osu_level = ?`
                            ).bind(
                                ...bindings, 
                                user.username, user.country?.code || "XX", user.avatar_url, user.cover?.url || "", now,
                                user.statistics?.ranked_score || 0, user.statistics?.play_count || 0, user.statistics?.level?.current || 0
                            ).run();
                        }
                        await env.ONLINE_KV.put(lastSyncKey, now.toString());
                    }
                }
            } catch (e) { }
        }

        // Maintain Session (Hardware ID -> osu_id link)
        const sessCacheKey = `hw_link:${userId}`;
        const cachedOsuId = await env.ONLINE_KV.get(sessCacheKey);
        const currentOsuId = osuId ? osuId.toString() : "";

        if (cachedOsuId !== currentOsuId) {
            await env.DB.prepare(
                `INSERT INTO sessions (hardware_id, osu_id, last_seen) 
                 VALUES (?, ?, ?) 
                 ON CONFLICT(hardware_id) DO UPDATE SET 
                   osu_id = ?, last_seen = ?`
            ).bind(userId, osuId, now, osuId, now).run();
            await env.ONLINE_KV.put(sessCacheKey, currentOsuId);
        }
        
        return new Response(JSON.stringify({ status: "ok" }), {
          headers: { ...corsHeaders, "Content-Type": "application/json" }
        });
      } catch (e) {
        return new Response(JSON.stringify({ error: e.message }), { status: 500, headers: corsHeaders });
      }
    }
    
    // Stats Endpoint: Simplified to only show Total Registered Users
    if (url.pathname === "/stats" && request.method === "GET") {
      try {
          const totalResult = await env.DB.prepare("SELECT COUNT(*) as count FROM accounts WHERE total_plays > 0").first();
          return new Response(JSON.stringify({
            total_players: totalResult.count || 0
          }), { headers: { ...corsHeaders, "Content-Type": "application/json" } });
      } catch (e) { return new Response(JSON.stringify({ error: e.message }), { status: 500, headers: corsHeaders }); }
    }

    if (url.pathname === "/leaderboard" && request.method === "GET") {
      try {
          const search = url.searchParams.get("search") || "";
          const sort = url.searchParams.get("sort") || "total_plays"; 
          const order = url.searchParams.get("order") || "DESC";
          
          const allowedSorts = ["total_plays", "avg_pp", "avg_acc", "avg_ur", "streak", "last_seen", "osu_ranked_score", "osu_play_count"];
          const finalSort = allowedSorts.includes(sort) ? sort : "total_plays";
          const finalOrder = order.toUpperCase() === "ASC" ? "ASC" : "DESC";

          let query = `SELECT osu_id, username, country, avatar_url, cover_url, total_plays, total_time, avg_pp, avg_acc, avg_ur, form, streak, last_seen, osu_ranked_score, perf_match 
                       FROM accounts WHERE total_plays > 0`;
          const params = [];
          
          if (search) {
              query += " AND username LIKE ?";
              params.push(`%${search}%`);
          }
          
          query += ` ORDER BY ${finalSort} ${finalOrder} LIMIT 100`;
          
          const { results } = await env.DB.prepare(query).bind(...params).all();
          return new Response(JSON.stringify(results), { headers: { ...corsHeaders, "Content-Type": "application/json" } });
      } catch (e) { return new Response(JSON.stringify({ error: e.message }), { status: 500, headers: corsHeaders }); }
    }

    if (url.pathname.startsWith("/profile/u/") && request.method === "GET") {
        try {
            const username = url.pathname.split("/").pop();
            const result = await env.DB.prepare(
                `SELECT * FROM accounts WHERE username = ? COLLATE NOCASE`
            ).bind(decodeURIComponent(username)).first();
            
            if (!result) {
                return new Response(JSON.stringify({ error: "User not found" }), { status: 404, headers: corsHeaders });
            }
            
            return new Response(JSON.stringify(result), { headers: { ...corsHeaders, "Content-Type": "application/json" } });
        } catch (e) { return new Response(JSON.stringify({ error: e.message }), { status: 500, headers: corsHeaders }); }
    }

    return new Response("Not Found", { status: 404, headers: corsHeaders });
  }
};
