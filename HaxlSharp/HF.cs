﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;

namespace HaxlSharp
{
    public interface Fetch<A>
    {
        IEnumerable<BindProjectPair> CollectedExpressions { get; }
        LambdaExpression Initial { get; }
        SplitFetch<A> Split();
        Task<A> FetchWith(Fetcher fetcher, int nestLevel = 0);
        Fetch ToFetch(string bindTo, Scope scope);
    }

    public interface SplitFetch<A>
    {
        X Run<X>(SplitHandler<A, X> handler);
    }

    public interface SplitHandler<A, X>
    {
        X Bind(SplitBind<A> splits);
        X Request(Returns<A> request, Type requestType);
        X RequestSequence<B, Item>(IEnumerable<B> list, Func<B, Fetch<Item>> bind);
        X Select<B>(Fetch<B> fetch, Expression<Func<B, A>> fmap);
        X Result(A result);
    }

    public interface FetchSplitter<C>
    {
        SplitFetch<C> Bind<A, B>(Fetch<C> bind);
        SplitFetch<C> Pass(Fetch<C> unsplittable);
    }

    public class Bind<A, B, C> : Fetch<C>
    {
        public Bind(IEnumerable<BindProjectPair> binds, Fetch<A> expr)
        {
            _binds = binds;
            Fetch = expr;
        }

        private readonly IEnumerable<BindProjectPair> _binds;
        public IEnumerable<BindProjectPair> CollectedExpressions { get { return _binds; } }

        public LambdaExpression Initial
        {
            get { return Fetch.Initial; }
        }

        public readonly Fetch<A> Fetch;

        public SplitFetch<C> Split()
        {
            var splitter = new Splitta<C>();
            return splitter.Bind<A, B>(this);
        }

        public Fetch ToFetch(string bindTo, Scope scope)
        {
            var bindSplit = Split();
            return Splitter.ToFetch((SplitBind<C>)bindSplit, bindTo, new Scope(scope));
        }

        public Task<C> FetchWith(Fetcher fetcher, int nestLevel)
        {
            var split = Split();
            var runner = new SplitRunner<C>(fetcher, nestLevel);
            return split.Run(runner);
        }
    }

    public class BoundExpression
    {
        public readonly LambdaExpression Expression;
        public readonly string BindVariable;
        public BoundExpression(LambdaExpression expression, string bindVariable)
        {
            Expression = expression;
            BindVariable = bindVariable;
        }
    }

    public class BindProjectPair
    {
        public BindProjectPair(LambdaExpression bind, LambdaExpression project)
        {
            Bind = bind;
            Project = project;
        }

        public readonly LambdaExpression Bind;
        public readonly LambdaExpression Project;
    }

    public class ApplicativeGroup
    {
        public ApplicativeGroup(bool isProjectGroup = false, List<LambdaExpression> expressions = null, List<string> boundVariables = null)
        {
            Expressions = expressions ?? new List<LambdaExpression>();
            BoundVariables = boundVariables ?? new List<string>();
            IsProjectGroup = isProjectGroup;
        }

        public readonly List<LambdaExpression> Expressions;
        public readonly List<string> BoundVariables;
        public List<BoundExpression> BoundExpressions;
        public readonly bool IsProjectGroup;
    }

    public abstract class FetchNode<A> : Fetch<A>, SplitFetch<A>
    {
        private static readonly IEnumerable<BindProjectPair> emptyList = new List<BindProjectPair>();
        public IEnumerable<BindProjectPair> CollectedExpressions { get { return emptyList; } }
        public LambdaExpression Initial { get { return Expression.Lambda(Expression.Constant(this)); } }

        public abstract X Run<X>(SplitHandler<A, X> handler);

        public SplitFetch<A> Split()
        {
            var splitter = new Splitta<A>();
            return splitter.Pass(this);
        }

        public Task<A> FetchWith(Fetcher fetcher, int nestLevel)
        {
            var split = Split();
            var runner = new SplitRunner<A>(fetcher, nestLevel);
            return split.Run(runner);
        }

        public virtual Fetch ToFetch(string bindTo, Scope scope)
        {
            throw new NotImplementedException();
        }
    }

    public class Request<A> : FetchNode<A>
    {
        public readonly Returns<A> request;
        public Request(Returns<A> request)
        {
            this.request = request;
        }

        public override Fetch ToFetch(string bindTo, Scope scope)
        {
            return Fetch.FromFunc(() =>
            {
                var blocked = new BlockedRequest(request, request.GetType(), bindTo);
                return Blocked.New(
                    new List<BlockedRequest> { blocked },
                    Fetch.FromFunc(() => Done.New(_ =>
                    {
                        var result = blocked.Resolver.Task.Result;
                        return scope.Add(bindTo, result);
                    }))
                );
            });
        }

        public Type RequestType { get { return request.GetType(); } }

        public override X Run<X>(SplitHandler<A, X> handler)
        {
            return handler.Request(request, RequestType);
        }
    }

    public class RequestSequence<A, B> : FetchNode<IEnumerable<B>>
    {
        public readonly IEnumerable<A> List;
        public readonly Func<A, Fetch<B>> Bind;
        public RequestSequence(IEnumerable<A> list, Func<A, Fetch<B>> bind)
        {
            List = list;
            Bind = bind;
        }

        public override Fetch ToFetch(string bindTo, Scope parentScope)
        {
            var childScope = new Scope(parentScope);
            var fetches = List.Select(Bind)
                              .Select((f, i) => f.ToFetch(i.ToString(), childScope));

            var concurrent = fetches.Aggregate((f1, f2) => f1.Applicative(f2));
            return concurrent.Bind(scope => Fetch.FromFunc(() => Done.New(_ =>
            {
                var values = scope.ShallowValues.Select(v => (B)v);
                return scope.WriteParent(bindTo, values);
            }
            )));
        }

        public override X Run<X>(SplitHandler<IEnumerable<B>, X> handler)
        {
            return handler.RequestSequence(List, Bind);
        }
    }

    public class Select<A, B> : FetchNode<B>
    {
        public readonly Fetch<A> Fetch;
        public readonly Expression<Func<A, B>> Map;
        public Select(Fetch<A> fetch, Expression<Func<A, B>> map)
        {
            Fetch = fetch;
            Map = map;
        }

        public override X Run<X>(SplitHandler<B, X> handler)
        {
            return handler.Select(Fetch, Map);
        }

    }

    public class FetchResult<A> : FetchNode<A>
    {
        public A Val;

        public object Value
        {
            get
            {
                return Val;
            }
        }

        public FetchResult(A val)
        {
            Val = val;
        }

        public override X Run<X>(SplitHandler<A, X> handler)
        {
            return handler.Result(Val);
        }
    }

    public interface HoldsObject
    {
        object Value { get; }
    }

    public static class ExprExt
    {
        public static Fetch<B> Select<A, B>(this Fetch<A> self, Expression<Func<A, B>> f)
        {
            return new Select<A, B>(self, f);
        }

        public static Fetch<C> SelectMany<A, B, C>(this Fetch<A> self, Expression<Func<A, Fetch<B>>> bind, Expression<Func<A, B, C>> project)
        {
            var bindExpression = new BindProjectPair(bind, project);
            var newBinds = self.CollectedExpressions.Append(bindExpression);
            return new Bind<A, B, C>(newBinds, self);
        }

        /// <summary>
        /// Default to using recursion depth limit of 100
        /// </summary>
        public static Fetch<IEnumerable<B>> SelectFetch<A, B>(this IEnumerable<A> list, Func<A, Fetch<B>> bind)
        {
            return new RequestSequence<A, B>(list, bind);
        }

    }

}
